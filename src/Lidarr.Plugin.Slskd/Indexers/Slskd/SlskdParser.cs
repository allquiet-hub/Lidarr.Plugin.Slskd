using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using NLog;
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
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

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
                _logger.Warn($"Search {searchResult.Id} was still running after {_settings.SearchTimeout}s, abandoning it");
                CancelSearch(searchResult.Id);
                return new List<ReleaseInfo>();
            }

            // Re-fetch with responses: slskd withholds the response bodies until the search completes
            searchResult = GetSearchResult(searchResult.Id, includeResponses: true);

            return ProcessSearchResults(searchResult, GetExpectedTrackCount(indexerResponse.HttpRequest));
        }

        private static int GetExpectedTrackCount(HttpRequest request)
        {
            var header = request?.Headers?[SlskdRequestGenerator.ExpectedTrackCountHeader];
            return int.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
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
            var budget = TimeSpan.FromSeconds(Math.Max(_settings.SearchTimeout, 1));
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < budget)
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

            request.RateLimit = _rateLimit;

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

        private IList<ReleaseInfo> ProcessSearchResults(SearchResult searchResult, int expectedTrackCount)
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

                ProcessUserResponse(response, searchResult.Id, expectedTrackCount, releases);
            }

            return releases.OrderByDescending(r => r.Size).ToList();
        }

        private void ProcessUserResponse(SearchResponse response, string searchId, int expectedTrackCount, List<ReleaseInfo> releases)
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

                var releaseInfo = CreateReleaseInfo(audioFiles, response, searchId, groupKey, expectedTrackCount);
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

        private ReleaseInfo CreateReleaseInfo(List<SlskdFile> audioFiles, SearchResponse response, string searchId, string groupKey, int expectedTrackCount)
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
                Title = FileProcessingUtils.BuildTitle(audioFiles),
                DownloadUrl = downloadPath,
                InfoUrl = $"{_settings.BaseUrl}searches/{searchId}",
                Size = totalSize,
                Source = response.Username,
                Origin = searchId,
                DownloadProtocol = nameof(SlskdDownloadProtocol),
            };

            if (response.UploadSpeed > 0)
            {
                var uploadDurationMinutes = Math.Max(1, (totalSize / (double)response.UploadSpeed) / 60.0);

                // Subtract so faster uploads (smaller duration) produce a more recent PublishDate
                // and are ranked first by Lidarr's newest-first sort
                releaseInfo.PublishDate = DateTime.UtcNow.AddMinutes(-uploadDurationMinutes);
            }

            return releaseInfo;
        }
    }
}
