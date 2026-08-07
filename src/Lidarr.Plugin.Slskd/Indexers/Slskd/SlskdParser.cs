using System;
using System.Collections.Concurrent;
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

        private static readonly TimeSpan ResponseSettleBudget = TimeSpan.FromSeconds(8);

        /// <summary>
        /// How long a search that looks empty is polled before it is believed, when nothing about it
        /// says otherwise. A search whose results have not been written yet is indistinguishable from
        /// one nobody answered, and queries that genuinely find nothing are common enough that they
        /// cannot be made to wait out the full settle budget. The window has to clear the gap between
        /// a search completing and its responses becoming readable, which grows with the size of the
        /// result: a search returning some ten thousand files takes a couple of seconds.
        /// </summary>
        private static readonly TimeSpan EmptyConfirmationBudget = TimeSpan.FromSeconds(4);

        private static readonly TimeSpan ResponseSettleInterval = TimeSpan.FromMilliseconds(600);

        /// <summary>
        /// Ceiling on a single search, not a tuning knob: what governs how long a search runs is the
        /// inactivity window sent to slskd, and searches ordinarily complete within seconds. This
        /// only stops a search whose results never stop trickling in from stalling the whole chain.
        /// </summary>
        private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Own username per slskd instance, so that this instance's own shares are never offered back
        /// as releases. It is read from slskd instead of being configured because slskd already knows
        /// it. The options endpoint is the source: the server endpoint looks like the better one since
        /// it describes the live connection, and its schema does declare a username, but the field is
        /// absent from what it actually serialises. Cached because a parser is built for every search.
        /// </summary>
        private static readonly ConcurrentDictionary<string, (string Username, DateTime ResolvedAt)> LocalUsernames =
            new ConcurrentDictionary<string, (string Username, DateTime ResolvedAt)>();

        private static readonly TimeSpan LocalUsernameLifetime = TimeSpan.FromHours(1);

        /// <summary>
        /// Characters Lidarr's SimpleTitleRegex deletes from a title before the criteria regex runs,
        /// while the pattern built from the artist's name keeps them as escaped literals.
        /// </summary>
        private static readonly char[] CriteriaUnmatchableChars = { '*', '<', '>', '|' };

        private static readonly Regex WhitespaceRegex = new (@"\s+", RegexOptions.Compiled);

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
            searchResult = FetchSettledSearchResult(searchResult);

            return ProcessSearchResults(
                searchResult,
                GetExpectedTrackCount(indexerResponse.HttpRequest),
                DecodeHeader(indexerResponse.HttpRequest, SlskdRequestGenerator.ArtistNameHeader),
                DecodeHeader(indexerResponse.HttpRequest, SlskdRequestGenerator.AlbumTitleHeader),
                GetAlbumYear(indexerResponse.HttpRequest),
                GetHeaderInt(indexerResponse.HttpRequest, SlskdRequestGenerator.MaximumTrackCountHeader));
        }

        private static int GetExpectedTrackCount(HttpRequest request)
        {
            var header = request?.Headers?[SlskdRequestGenerator.ExpectedTrackCountHeader];
            return int.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
        }

        private static int GetAlbumYear(HttpRequest request)
        {
            var header = request?.Headers?[SlskdRequestGenerator.AlbumYearHeader];
            return int.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : 0;
        }

        private static int GetHeaderInt(HttpRequest request, string name)
        {
            var header = request?.Headers?[name];
            return int.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
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
        private static string EnsureMappableTitle(string title, string artistName, string albumTitle, int albumYear)
        {
            if (artistName.IsNullOrWhiteSpace() || albumTitle.IsNullOrWhiteSpace() || title.IsNullOrWhiteSpace())
            {
                return title;
            }

            // An artist or album containing any of '* < > |' (DECO*27, while(1<2)) can never satisfy
            // the criteria parser: Lidarr deletes those characters from the title before matching, but
            // escapes them as literals in the pattern built from the library's own names, so no title
            // can contain what the pattern demands. The escape hatch is the standard parser, which
            // needs the dash-separated 'Artist - Album (Year)' shape and maps the halves through
            // normalised lookups that drop those characters from both sides.
            if ((artistName.IndexOfAny(CriteriaUnmatchableChars) >= 0 || albumTitle.IndexOfAny(CriteriaUnmatchableChars) >= 0) && albumYear > 0)
            {
                // Brackets are flattened out of the album because the standard parser stops the album
                // capture at the first parenthesis: '愛迷エレジー (Reloaded)' parses as just '愛迷エレジー'
                // and maps to the base album. Without them the full words survive the capture, and the
                // album lookup normalises brackets away anyway when comparing titles.
                var flatAlbum = WhitespaceRegex.Replace(albumTitle.Replace('(', ' ').Replace(')', ' ').Replace('[', ' ').Replace(']', ' '), " ").Trim();
                return $"{artistName} - {flatAlbum} ({albumYear}) {title}";
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
                // Appending the album makes ANY folder map to the album being searched, including one
                // whose name plainly says it is a different record — which then dodges the wrong-album
                // rejection precisely because without the annotation it would not have parsed at all.
                // The annotation is therefore earned, not free: the title must already resemble the
                // album, with punctuation forgiven for normal titles and taken literally for
                // degenerate ones a couple of characters long, where the forgiving comparison would
                // match practically anything.
                if (!TitleResemblesAlbum(annotated, albumTitle))
                {
                    return title;
                }

                annotated = $"{annotated} [{album}]";
            }

            return criteriaRegex.IsMatch(annotated) ? annotated : title;
        }

        private static bool TitleResemblesAlbum(string title, string albumTitle)
        {
            var normalizedAlbum = Normalize(albumTitle);

            if (normalizedAlbum.Length >= 3)
            {
                return Normalize(title).Contains(normalizedAlbum, StringComparison.Ordinal);
            }

            var literal = albumTitle.Trim();
            return literal.Length > 0 && title.Contains(literal, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var character in value.RemoveAccent())
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Fetches the responses of a completed search, waiting out slskd's write-behind.
        ///
        /// slskd marks a search complete before it has flushed the responses to its store: fetched in
        /// the same instant, the payload comes back empty or partial with no error, complete only
        /// moments later. The payload is therefore refetched until it stops growing, and a search is
        /// believed to be empty only once it has stayed empty for the confirmation window.
        /// </summary>
        private SearchResult FetchSettledSearchResult(SearchResult completed)
        {
            var best = TryGetSearchResult(completed.Id);
            var bestCount = best?.Responses?.Count ?? 0;

            // The response count is written behind the search itself, so a search that drew a thousand
            // peers can still report none at the moment it completes. Whatever any reading says the
            // search holds is therefore a floor, never a ceiling, and never a reason to stop early.
            var expected = Math.Max(completed.ResponseCount, best?.ResponseCount ?? 0);
            var hasResults = FoundSomething(completed) || FoundSomething(best);

            var stopwatch = Stopwatch.StartNew();

            while (bestCount < expected || (bestCount == 0 && (hasResults || stopwatch.Elapsed < EmptyConfirmationBudget)))
            {
                if (stopwatch.Elapsed >= ResponseSettleBudget)
                {
                    break;
                }

                Thread.Sleep(ResponseSettleInterval);

                var refetched = TryGetSearchResult(completed.Id);
                var count = refetched?.Responses?.Count ?? 0;
                expected = Math.Max(expected, refetched?.ResponseCount ?? 0);
                hasResults |= FoundSomething(refetched);

                if (count > bestCount)
                {
                    best = refetched;
                    bestCount = count;
                    continue;
                }

                // A payload that has stopped growing has settled. An empty one has not: it means the
                // responses are still being written, so keep polling until the budget runs out.
                if (bestCount > 0)
                {
                    break;
                }
            }

            return best ?? completed;
        }

        /// <summary>
        /// Whether a search is known to have found something, however little its counters admit to at
        /// the moment they are read. A search that ended because it reached a limit necessarily found
        /// enough to reach it, and that reason is decided when the search stops rather than when its
        /// results are written down, which makes it the one signal the write-behind cannot flatten.
        /// </summary>
        private static bool FoundSomething(SearchResult search)
        {
            if (search == null)
            {
                return false;
            }

            return search.ResponseCount > 0 ||
                   search.FileCount > 0 ||
                   search.State?.Contains("LimitReached", StringComparison.OrdinalIgnoreCase) == true;
        }

        /// <summary>
        /// Fetches a search with its responses, treating a failed fetch as "nothing new this time"
        /// rather than an error. The payload of a search that drew a thousand peers is large enough to
        /// fail on its own under load, and letting that failure escape would discard every release of
        /// the whole album, including the ones other queries already found.
        /// </summary>
        private SearchResult TryGetSearchResult(string searchId)
        {
            try
            {
                return GetSearchResult(searchId, includeResponses: true);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, $"Could not fetch the responses of search {searchId}, retrying while the budget lasts");
                return null;
            }
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

        private bool IsIgnoredUser(string username)
        {
            if (_ignoredUsersSet.Contains(username))
            {
                return true;
            }

            var localUsername = GetLocalUsername();

            return !localUsername.IsNullOrWhiteSpace() && localUsername.Equals(username, StringComparison.OrdinalIgnoreCase);
        }

        private string GetLocalUsername()
        {
            var instance = _settings.BaseUrl ?? string.Empty;

            if (LocalUsernames.TryGetValue(instance, out var cached) && DateTime.UtcNow - cached.ResolvedAt < LocalUsernameLifetime)
            {
                return cached.Username;
            }

            string username = null;

            try
            {
                var request = new HttpRequestBuilder(_settings.BaseUrl)
                    .Resource("api/v0/options")
                    .Accept(HttpAccept.Json)
                    .SetHeader("X-API-Key", _settings.ApiKey)
                    .Build();

                username = new HttpResponse<SlskdOptions>(_httpClient.Get(request)).Resource?.Soulseek?.Username;
            }
            catch (Exception ex)
            {
                // Only the automatic exclusion is lost, so the search is worth continuing without it
                _logger.Debug(ex, "Could not read the slskd username, only the configured users are ignored");
            }

            // Stored even when it could not be resolved, so that an instance which does not answer is
            // not asked again on every search
            LocalUsernames[instance] = (username, DateTime.UtcNow);

            return username;
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

        private IList<ReleaseInfo> ProcessSearchResults(SearchResult searchResult, int expectedTrackCount, string artistName, string albumTitle, int albumYear, int maximumTrackCount)
        {
            var releases = new List<ReleaseInfo>();

            if (searchResult.Responses == null || !searchResult.Responses.Any())
            {
                _logger.Debug("Search {0} returned no responses.", searchResult.Id);
                return releases;
            }

            foreach (var response in searchResult.Responses)
            {
                if (IsIgnoredUser(response.Username))
                {
                    _logger.Debug($"Ignored response from user {response.Username}");
                    continue;
                }

                ProcessUserResponse(response, searchResult.Id, expectedTrackCount, artistName, albumTitle, albumYear, maximumTrackCount, releases);
            }

            return releases.OrderByDescending(r => r.Size).ToList();
        }

        private void ProcessUserResponse(SearchResponse response, string searchId, int expectedTrackCount, string artistName, string albumTitle, int albumYear, int maximumTrackCount, List<ReleaseInfo> releases)
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

                var releaseInfo = CreateReleaseInfo(audioFiles, response, searchId, groupKey, expectedTrackCount, artistName, albumTitle, albumYear, maximumTrackCount);
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

        private ReleaseInfo CreateReleaseInfo(List<SlskdFile> audioFiles, SearchResponse response, string searchId, string groupKey, int expectedTrackCount, string artistName, string albumTitle, int albumYear, int maximumTrackCount)
        {
            var isSingleFile = audioFiles.Count == 1;
            var downloadPath = isSingleFile ? audioFiles[0].FileName : groupKey;
            var identifier = ReleaseIdentifier.ForRelease(response.Username, groupKey);

            var totalSize = audioFiles.Sum(file => file.Size);
            var releaseInfo = new SlskdReleaseInfo
            {
                AudioFileCount = audioFiles.Count,
                ExpectedTrackCount = expectedTrackCount,
                MaximumTrackCount = maximumTrackCount,
                Guid = identifier,
                Title = EnsureMappableTitle(
                    FileProcessingUtils.BuildTitle(audioFiles) + DescribePeer(response),
                    artistName,
                    albumTitle,
                    albumYear),
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
