using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Plugin.Slskd.Helpers;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Core.Indexers.Slskd
{
    public class SlskdParser : IParseIndexerResponse
    {
        private const int BytesPerMegabyte = 1024 * 1024;

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

        /// <summary>
        /// Ceiling on a single search, not a tuning knob: what governs how long a search runs is the
        /// inactivity window sent to slskd, and observed searches complete in under ten seconds. This
        /// only stops a search whose results never stop trickling in from stalling the whole chain.
        /// </summary>
        private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(30);

        private readonly ProviderDefinition _definition;
        private readonly SlskdIndexerSettings _settings;
        private readonly TimeSpan _rateLimit;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly HashSet<string> _ignoredUsersSet;

        public SlskdParser(ProviderDefinition definition, SlskdIndexerSettings settings, TimeSpan rateLimit, IHttpClient httpClient, Logger logger)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _rateLimit = rateLimit;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _ignoredUsersSet = new HashSet<string>(
                settings.IgnoredUsers?.Select(u => u.Value) ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            if (indexerResponse?.HttpResponse == null)
            {
                throw new ArgumentNullException(nameof(indexerResponse));
            }

            var searchResult = GetInitialSearchResult(indexerResponse);

            if (!WaitForSearchCompletion(searchResult.Id))
            {
                // Abandoning the tier is better than throwing: a slow search would otherwise fail the
                // whole feed and mark the indexer unhealthy, when the next query may well succeed
                _logger.Warn($"Search {searchResult.Id} was still running after {SearchBudget.TotalSeconds}s, abandoning it");
                CancelSearch(searchResult.Id);
                return new List<ReleaseInfo>();
            }

            // Re-fetch with responses: slskd withholds the response bodies until the search completes
            searchResult = GetSearchResult(searchResult.Id, includeResponses: true);

            return ProcessSearchResults(
                searchResult,
                GetExpectedTrackCount(indexerResponse.HttpRequest),
                DecodeHeader(indexerResponse.HttpRequest, SlskdRequestGenerator.ArtistNameHeader),
                DecodeHeader(indexerResponse.HttpRequest, SlskdRequestGenerator.AlbumTitleHeader));
        }

        private static int GetExpectedTrackCount(HttpRequest request)
        {
            var header = request?.Headers?[SlskdRequestGenerator.ExpectedTrackCountHeader];
            return int.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
        }

        private static string DecodeHeader(HttpRequest request, string name)
        {
            var value = request?.Headers?[name];
            if (value.IsNullOrWhiteSpace())
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// Guarantees that Lidarr can tie the release to the album it was searched for.
        ///
        /// Every route from a release back to an artist and album goes through parsing a title: the
        /// decision engine parses the release title, and the queue parses the same title again out of
        /// history. The last-resort parser looks for the library's artist and album inside the title
        /// with their exact punctuation, so a folder named "The Signature Series Volume 1" can never
        /// satisfy it when the library says "The Signature Series, Volume 1:".
        ///
        /// When the title as built would not satisfy that parser, the library's own names are added to
        /// it — the artist in front, the album in brackets at the end — which makes the match hold by
        /// construction. Titles that already satisfy it are left alone.
        /// </summary>
        private static string EnsureMappableTitle(string title, string artistName, string albumTitle)
        {
            if (artistName.IsNullOrWhiteSpace() || albumTitle.IsNullOrWhiteSpace() || title.IsNullOrWhiteSpace())
            {
                return title;
            }

            // Mirrors Parser.ParseAlbumTitleWithSearchCriteria: accents are stripped from the names,
            // spaces match any separator, remaining punctuation is literal
            var artist = (artistName == "Various Artists" ? "VA" : artistName).RemoveAccent();
            var album = albumTitle.RemoveAccent();
            var escapedArtist = Regex.Escape(artist).Replace(@"\ ", @"[\W_]");
            var escapedAlbum = Regex.Escape(album).Replace(@"\ ", @"[\W_]");

            var criteriaRegex = new Regex(
                @"^(\W*|\b)(" + escapedArtist + @")(\W*|\b).*(\W*|\b)(" + escapedAlbum + @")(\W*|\b)",
                RegexOptions.IgnoreCase);

            if (criteriaRegex.IsMatch(title))
            {
                return title;
            }

            var annotated = Regex.IsMatch(title, @"^(\W*|\b)" + escapedArtist + @"(\W*|\b)", RegexOptions.IgnoreCase)
                ? title
                : $"{artist} {title}";

            if (!criteriaRegex.IsMatch(annotated))
            {
                annotated = $"{annotated} [{album}]";
            }

            return criteriaRegex.IsMatch(annotated) ? annotated : title;
        }

        private SearchResult GetInitialSearchResult(IndexerResponse indexerResponse)
        {
            var searchResult = new HttpResponse<SearchResult>(indexerResponse.HttpResponse).Resource;
            return searchResult ?? throw new InvalidOperationException("Failed to parse initial search result.");
        }

        /// <summary>
        /// Polls until slskd marks the search complete, giving up once the configured budget is spent.
        /// </summary>
        private bool WaitForSearchCompletion(string searchId)
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < SearchBudget)
            {
                if (GetSearchResult(searchId, includeResponses: false).IsComplete)
                {
                    return true;
                }

                // Without an explicit interval the loop is paced only by Lidarr's rate limiter, which
                // costs a lot of round trips for no gain
                Thread.Sleep(PollInterval);
            }

            return false;
        }

        private void CancelSearch(string searchId)
        {
            try
            {
                var request = new HttpRequestBuilder(_settings.BaseUrl)
                    .Resource($"api/v0/searches/{searchId}")
                    .SetHeader("X-API-Key", _settings.ApiKey)
                    .Build();

                request.Method = HttpMethod.Delete;
                _httpClient.Execute(request);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, $"Could not cancel search {searchId}");
            }
        }

        private SearchResult GetSearchResult(string searchId, bool includeResponses)
        {
            var request = new HttpRequestBuilder(_settings.BaseUrl)
                .Resource($"api/v0/searches/{searchId}/")
                .Accept(HttpAccept.Json)
                .SetHeader("X-API-Key", _settings.ApiKey)
                .AddQueryParam("includeResponses", includeResponses.ToString().ToLowerInvariant())
                .Build();

            try
            {
                var response = _httpClient.Get(request);
                var result = new HttpResponse<SearchResult>(response).Resource;
                if (result == null)
                {
                    throw new InvalidOperationException($"Failed to retrieve search result for ID: {searchId}");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error retrieving search result for ID: {searchId}");
                throw;
            }
        }

        private IList<ReleaseInfo> ProcessSearchResults(SearchResult searchResult, int expectedTrackCount, string artistName, string albumTitle)
        {
            var releases = new List<ReleaseInfo>();

            if (searchResult.Responses == null || !searchResult.Responses.Any())
            {
                _logger.Debug("Search {0} returned no responses.", searchResult.Id);
                return releases;
            }

            foreach (var response in searchResult.Responses)
            {
                if (_ignoredUsersSet.Contains(response.Username))
                {
                    _logger.Debug($"Ignored response from user {response.Username}");
                    continue;
                }

                ProcessUserResponse(response, searchResult.Id, expectedTrackCount, artistName, albumTitle, releases);
            }

            return releases.OrderByDescending(r => r.Size).ToList();
        }

        private void ProcessUserResponse(SearchResponse response, string searchId, int expectedTrackCount, string artistName, string albumTitle, List<ReleaseInfo> releases)
        {
            var rawGroups = response.Files
                .Cast<SlskdFile>()
                .GroupBy(file => file.ParentPath);

            foreach (var (groupKey, files) in MergeDiscFolders(rawGroups))
            {
                FileProcessingUtils.EnsureFileExtensions(files);
                var audioFiles = files.FilterValidAudioFiles().ToList();

                if (!IsValidAudioGroup(audioFiles, groupKey, response.Username))
                {
                    continue;
                }

                var releaseInfo = CreateReleaseInfo(audioFiles, response, searchId, groupKey, expectedTrackCount, artistName, albumTitle);
                if (releaseInfo != null)
                {
                    releases.Add(releaseInfo);
                }
            }
        }

        private static List<(string GroupKey, List<SlskdFile> Files)> MergeDiscFolders(
            IEnumerable<IGrouping<string, SlskdFile>> groups)
        {
            var merged = new Dictionary<string, List<SlskdFile>>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var parentPath = group.Key ?? string.Empty;
                var folderName = parentPath.Split('\\').LastOrDefault() ?? string.Empty;

                var groupKey = FileProcessingUtils.IsDiscFolder(folderName)
                    ? FileProcessingUtils.GetParentPath(parentPath)
                    : parentPath;

                if (!merged.TryGetValue(groupKey, out var list))
                {
                    merged[groupKey] = list = new List<SlskdFile>();
                }

                list.AddRange(group);
            }

            return merged.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        private bool IsValidAudioGroup(List<SlskdFile> audioFiles, string groupKey, string username)
        {
            if (audioFiles.Any())
            {
                return true;
            }

            _logger.Debug($"Ignored result {groupKey} from user {username}: no audio files found");
            return false;
        }

        private ReleaseInfo CreateReleaseInfo(List<SlskdFile> audioFiles, SearchResponse response, string searchId, string groupKey, int expectedTrackCount, string artistName, string albumTitle)
        {
            var isSingleFile = audioFiles.Count == 1;
            var downloadPath = isSingleFile ? audioFiles[0].FileName : groupKey;
            var identifier = Crc32Hasher.Crc32Base64($"{response.Username}{groupKey}");

            var totalSize = audioFiles.Sum(file => file.Size);
            var releaseInfo = new SlskdReleaseInfo
            {
                AudioFileCount = audioFiles.Count,
                ExpectedTrackCount = expectedTrackCount,
                Guid = identifier,
                Title = EnsureMappableTitle(
                    FileProcessingUtils.BuildTitle(audioFiles) + DescribePeer(response),
                    artistName,
                    albumTitle),
                DownloadUrl = downloadPath,
                InfoUrl = $"{_settings.BaseUrl}searches/{searchId}",
                Size = totalSize,

                // Soulseek search results carry no date, so the only truthful publish date is the moment
                // the result was seen. Nothing is lost by it: Lidarr compares age for usenet only.
                PublishDate = DateTime.UtcNow,
                Source = response.Username,
                Origin = searchId,
                DownloadProtocol = nameof(SlskdDownloadProtocol),
            };

            return releaseInfo;
        }

        /// <summary>
        /// Appends how well the peer can actually serve the release: its upload speed, and its queue when
        /// the transfer would not start straight away.
        ///
        /// This rides in the title because no field of ReleaseInfo reaches the interactive search view,
        /// and Lidarr's ranking ignores it: peers are compared for torrents only. It is a trailing
        /// annotation, in the same position as the codec and bitrate, and leaves the parse untouched.
        /// </summary>
        private static string DescribePeer(SearchResponse response)
        {
            if (response.UploadSpeed <= 0)
            {
                return string.Empty;
            }

            var speed = $"{response.UploadSpeed / (double)BytesPerMegabyte:0.#} MB/s";

            return response.QueueLength > 0
                ? $" [{speed}, queued behind {response.QueueLength}]"
                : $" [{speed}]";
        }
    }
}
