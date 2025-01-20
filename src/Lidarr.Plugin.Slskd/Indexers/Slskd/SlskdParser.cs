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
        private readonly ProviderDefinition _definition;
        private readonly SlskdIndexerSettings _settings;
        private readonly TimeSpan _rateLimit;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public SlskdParser(ProviderDefinition definition, SlskdIndexerSettings settings, TimeSpan rateLimit, IHttpClient httpClient, Logger logger)
        {
            _definition = definition;
            _settings = settings;
            _rateLimit = rateLimit;
            _httpClient = httpClient;
            _logger = logger;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            var searchRequest = Json.Deserialize<SearchRequest>(indexerResponse.HttpRequest.ContentSummary);
            var searchResult = new HttpResponse<SearchResult>(indexerResponse.HttpResponse).Resource;
            if (searchResult == null)
            {
                throw new Exception("Failed to parse search result.");
            }

            var searchTimeout = (searchRequest?.SearchTimeout ?? _settings.SearchTimeout) + 5; // Add 5 seconds buffer
            WaitForSearchCompletion(searchResult.Id, searchTimeout * 1000);

            // Re-fetch the search result with responses
            searchResult = GetSearchResult(searchResult.Id, includeResponses: true);

            // Convert results to ReleaseInfo
            return ToReleaseInfo(searchResult, searchRequest?.MinimumResponseFileCount).OrderByDescending(o => o.Size).ToArray();
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
            }

            throw new TimeoutException($"Search {searchId} did not complete within the specified timeout.");
        }

        private SearchResult GetSearchResult(string searchId, bool includeResponses)
        {
            var request = RequestBuilder()
                .Resource($"api/v0/searches/{searchId}")
                .AddQueryParam("includeResponses", includeResponses.ToString().ToLowerInvariant())
                .Build();

            var response = _httpClient.Execute(request);
            return new HttpResponse<SearchResult>(response).Resource ??
                   throw new Exception("Failed to retrieve search result.");
        }

        private IEnumerable<ReleaseInfo> ToReleaseInfo(SearchResult searchResult, int? minimumFileCountInRelease)
        {
            foreach (var response in searchResult.Responses)
            {
                if (_settings.IgnoredUsers.Any(u => u.Value == response.Username))
                {
                    _logger.Info($"Ignored response from user {response.Username}.");
                    continue;
                }

                var groupedFiles = response.Files.GroupBy(file => file.ParentPath);

                foreach (var group in groupedFiles)
                {
                    var files = group.ToList();
                    var isSingleFile = files.Count == 1;
                    FileProcessingUtils.EnsureFileExtensions(files);
                    var audioFiles = files.FilterValidAudioFiles();
                    if (!audioFiles.Any())
                    {
                        _logger.Debug($"Ignored result {group.Key} from user {response.Username} because no audio files were found.");
                        continue;
                    }

                    if (minimumFileCountInRelease != null && audioFiles.Count < minimumFileCountInRelease)
                    {
                        _logger.Debug($"Ignored result {group.Key} from user {response.Username} because {group.Count()} audio files did not meet the minimum requirement of {minimumFileCountInRelease}.");
                        continue;
                    }

                    var totalSize = audioFiles.Sum(file => file.Size);
                    var releaseInfo = new ReleaseInfo
                    {
                        Guid = $"{response.Username}\\{group.Key}",
                        Title = FileProcessingUtils.BuildTitle(audioFiles),
                        DownloadUrl = isSingleFile ? audioFiles[0]?.FileName : audioFiles[0]?.ParentPath,
                        InfoUrl = $"{_settings.BaseUrl}searches/{searchResult.Id}",
                        Size = totalSize,
                        Source = response.Username,
                        Origin = searchResult.Id,
                        DownloadProtocol = nameof(SlskdDownloadProtocol)
                    };
                    if (response.UploadSpeed > 0)
                    {
                        releaseInfo.PublishDate = DateTime.Now.AddSeconds(-(totalSize / response.UploadSpeed));
                    }

                    yield return releaseInfo;
                }
            }
        }

        private HttpRequestBuilder RequestBuilder()
        {
            return new HttpRequestBuilder(_settings.BaseUrl)
                .Accept(HttpAccept.Json)
                .WithRateLimit(1) // Optional: Ensure requests respect the rate limit
                .SetHeader("X-API-Key", _settings.ApiKey);
        }
    }
}
