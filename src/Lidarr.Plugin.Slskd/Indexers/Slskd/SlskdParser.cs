using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
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
            var torrentInfos = new List<ReleaseInfo>();

            var jsonResponse = new HttpResponse<SearchResult>(indexerResponse.HttpResponse);
            var searchRequest =
                JsonConvert.DeserializeObject<SearchRequest>(indexerResponse.HttpRequest.ContentSummary);
            var searchResult = jsonResponse.Resource;
            var searchId = searchResult.Id;

            var searchTimeout = searchRequest.SearchTimeout ?? _settings.SearchTimeout;
            searchTimeout += 5; // Add 5 seconds buffer
            WaitForSearchCompletion(searchId, searchTimeout * 1000);

            searchResult = GetSearchResult(searchId, includeResponses: true);
            torrentInfos.AddRange(ToReleaseInfo(searchResult));

            return torrentInfos.OrderByDescending(o => o.Size).ToArray();
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

            throw new TimeoutException("Search did not complete within the specified timeout.");
        }

        private SearchResult GetSearchResult(string searchId, bool includeResponses)
        {
            var request = RequestBuilder()
                .Resource($"api/v0/searches/{searchId}")
                .AddQueryParam("includeResponses", includeResponses.ToString().ToLower())
                .Build();

            var searchResult = new HttpResponse<SearchResult>(_httpClient.Execute(request)).Resource;
            return searchResult ?? throw new Exception("Failed to query the search result.");
        }

        private IList<ReleaseInfo> ToReleaseInfo(SearchResult searchResult)
        {
            var releaseInfos = new List<ReleaseInfo>();

            foreach (var response in searchResult.Responses)
            {
                var groupedFiles = response.Files.GroupBy(file => file.ParentPath);

                foreach (var group in groupedFiles)
                {
                    var files = group.ToList();
                    var isSingleFileInParentDirectory = files.Count == 1;

                    FileProcessingUtils.EnsureFileExtensions(files);

                    // Filter valid audio files
                    var audioFiles = files
                        .Where(file =>
                            !string.IsNullOrEmpty(file.Extension) &&
                            FileProcessingUtils.ValidAudioExtensions.Contains(file.Extension))
                        .ToList();

                    // Skip if no valid audio files
                    if (!audioFiles.Any())
                    {
                        continue;
                    }

                    var downloadUrl = isSingleFileInParentDirectory ? audioFiles.FirstOrDefault()?.FileName : audioFiles.FirstOrDefault()?.ParentPath;
                    var title = FileProcessingUtils.BuildTitle(audioFiles);

                    // Create and add ReleaseInfo objects
                    var releaseInfo = new ReleaseInfo
                    {
                        Guid = Guid.NewGuid().ToString(),
                        Title = title,
                        DownloadUrl = downloadUrl,
                        InfoUrl = $"{_settings.BaseUrl}searches/{searchResult.Id}",
                        Size = audioFiles.Sum(f => f.Size),
                        Source = response.Username,
                        Origin = searchResult.Id,
                        DownloadProtocol = nameof(SlskdDownloadProtocol)
                    };
                    releaseInfos.Add(releaseInfo);
                }
            }

            return releaseInfos;
        }

        private HttpRequestBuilder RequestBuilder()
        {
            return new HttpRequestBuilder(_settings.BaseUrl)
                .Accept(HttpAccept.Json)
                .WithRateLimit(1)
                .SetHeader("X-API-Key", _settings.ApiKey);
        }
    }
}
