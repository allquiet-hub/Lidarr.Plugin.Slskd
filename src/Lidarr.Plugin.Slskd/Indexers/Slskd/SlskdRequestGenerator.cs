using System.Collections.Generic;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Core.Indexers.Slskd
{
    public sealed class SlskdRequestGenerator : IIndexerRequestGenerator
    {
        public SlskdIndexerSettings Settings { get; init; }
        public Logger Logger { get; set; }

        // Reusable HttpRequestBuilder to avoid re-initialization for every request
        private HttpRequestBuilder HttpRequestBuilder => new HttpRequestBuilder(Settings.BaseUrl)
            .Accept(HttpAccept.Json)
            .SetHeader("X-API-Key", Settings.ApiKey);

        public IndexerPageableRequestChain GetRecentRequests()
        {
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetRequests("no copyright music", searchTimeout: 2));
            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            if (searchCriteria != null)
            {
                chain.AddTier(GetRequests($"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}"));
            }

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            return new IndexerPageableRequestChain();
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters, int? searchTimeout = null)
        {
            searchTimeout ??= Settings.SearchTimeout * 1000; // Default to settings timeout
            var searchRequest = new SearchRequest()
            {
                SearchText = searchParameters,
                SearchTimeout = searchTimeout.Value,
                MinimumPeerUploadSpeed = Settings.MinimumPeerUploadSpeed * 1024 * 1024, // Convert MB/s to B/s
            };

            var request = BuildSearchRequest(searchRequest);
            yield return new IndexerRequest(request);
        }

        private HttpRequest BuildSearchRequest(SearchRequest searchRequest)
        {
            var json = searchRequest.ToJson();
            var request = HttpRequestBuilder
                .Resource("api/v0/searches")
                .Post()
                .Build();
            request.Headers.ContentType = "application/json";
            request.SetContent(json);
            request.ContentSummary = json;
            return request;
        }
    }
}
