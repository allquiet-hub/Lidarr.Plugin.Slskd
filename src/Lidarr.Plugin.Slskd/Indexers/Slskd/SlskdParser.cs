using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        // Constants
        private const int SearchTimeoutBuffer = 5;

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

            Json.TryDeserialize<SearchRequest>(indexerResponse.HttpRequest.ContentSummary, out var searchRequest);
            var searchResult = GetInitialSearchResult(indexerResponse);
            var searchTimeout = CalculateSearchTimeout(searchRequest);
            WaitForSearchCompletion(searchResult.Id, searchTimeout);

            // Re-fetch with responses
            searchResult = GetSearchResult(searchResult.Id, includeResponses: true);

            return ProcessSearchResults(searchResult, searchRequest?.MinimumResponseFileCount);
        }

        private SearchResult GetInitialSearchResult(IndexerResponse indexerResponse)
        {
            var searchResult = new HttpResponse<SearchResult>(indexerResponse.HttpResponse).Resource;
            if (searchResult == null)
            {
                throw new InvalidOperationException("Failed to parse initial search result.");
            }

            return searchResult;
        }

        private int CalculateSearchTimeout(SearchRequest searchRequest)
        {
            return ((searchRequest?.SearchTimeout ?? _settings.SearchTimeout) + SearchTimeoutBuffer) * 1000;
        }

        private void WaitForSearchCompletion(string searchId, int timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeout)
            {
                var searchResult = GetSearchResult(searchId, includeResponses: false);
                if (searchResult.IsComplete)
                {
                    return;
                }

                var request = new HttpRequest($"{_settings.BaseUrl}/api/v0/searches/{searchId}/status")
                    {
                        RateLimit = _rateLimit
                    };

                _httpClient.Get(request);
            }

            throw new TimeoutException($"Search {searchId} did not complete within {timeout}ms.");
        }

        private SearchResult GetSearchResult(string searchId, bool includeResponses)
        {
            var request = new HttpRequestBuilder(_settings.BaseUrl)
                .Resource($"api/v0/searches/{searchId}")
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

        private IList<ReleaseInfo> ProcessSearchResults(SearchResult searchResult, int? minimumFileCount)
        {
            var releases = new List<ReleaseInfo>();

            foreach (var response in searchResult.Responses)
            {
                if (_ignoredUsersSet.Contains(response.Username))
                {
                    _logger.Debug($"Ignored response from user {response.Username}");
                    continue;
                }

                ProcessUserResponse(response, searchResult.Id, minimumFileCount, releases);
            }

            return releases.OrderByDescending(r => r.Size).ToList();
        }

        private void ProcessUserResponse(SearchResponse response, string searchId, int? minimumFileCount, List<ReleaseInfo> releases)
        {
            var groupedFiles = response.Files
                .Cast<SlskdFile>()
                .GroupBy(file => file.ParentPath)
                .ToList();

            foreach (var group in groupedFiles)
            {
                var files = group.ToList();
                FileProcessingUtils.EnsureFileExtensions(files);
                var audioFiles = files.FilterValidAudioFiles().ToList();

                if (!IsValidAudioGroup(audioFiles, group.Key, response.Username, minimumFileCount))
                {
                    continue;
                }

                var releaseInfo = CreateReleaseInfo(audioFiles, response, searchId);
                if (releaseInfo != null)
                {
                    releases.Add(releaseInfo);
                }
            }
        }

        private bool IsValidAudioGroup(List<SlskdFile> audioFiles, string groupKey, string username, int? minimumFileCount)
        {
            if (!audioFiles.Any())
            {
                _logger.Debug($"Ignored result {groupKey} from user {username}: no audio files found");
                return false;
            }

            if (minimumFileCount.HasValue && audioFiles.Count < minimumFileCount)
            {
                _logger.Debug($"Ignored result {groupKey} from user {username}: " +
                            $"{audioFiles.Count} files < minimum {minimumFileCount}");
                return false;
            }

            return true;
        }

        private ReleaseInfo CreateReleaseInfo(List<SlskdFile> audioFiles, SearchResponse response, string searchId)
        {
            var isSingleFile = audioFiles.Count == 1;
            var totalSize = audioFiles.Sum(file => file.Size);
            var downloadPath = isSingleFile ? audioFiles[0].FileName : audioFiles[0].ParentPath;

            var releaseInfo = new ReleaseInfo
            {
                Guid = $"{response.Username}\\{downloadPath}",
                Title = FileProcessingUtils.BuildTitle(audioFiles),
                DownloadUrl = downloadPath,
                InfoUrl = $"{_settings.BaseUrl}searches/{searchId}",
                Size = totalSize,
                Source = response.Username,
                Origin = searchId,
                DownloadProtocol = nameof(SlskdDownloadProtocol)
            };

            if (response.UploadSpeed > 0)
            {
                releaseInfo.PublishDate = DateTime.Now.AddSeconds(-(totalSize / response.UploadSpeed));
            }

            return releaseInfo;
        }
    }
}
