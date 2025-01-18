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

        public IndexerPageableRequestChain GetRecentRequests()
        {
            var pageableRequests = new IndexerPageableRequestChain();

            var requests = GetRequests("Silent Partner Chances", searchTimeout: 2);
            pageableRequests.Add(requests);

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
            var chain = new IndexerPageableRequestChain();
            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters, int? searchTimeout = null)
        {
            searchTimeout = searchTimeout == null ? Settings.SearchTimeout * 1000 : searchTimeout.Value * 1000;
            var searchRequest = new SearchRequest(searchParameters, searchTimeout)
            {
                //MB/s to B/s
                MinimumPeerUploadSpeed = Settings.MinimumPeerUploadSpeed * 1024 * 1024
            };

            var request = RequestBuilder("api/v0/searches", searchRequest);
            yield return new IndexerRequest(request);
        }

        private HttpRequest RequestBuilder(string resource, SearchRequest searchRequest)
        {
            var request = new HttpRequestBuilder(Settings.BaseUrl)
                .Resource(resource)
                .Accept(HttpAccept.Json)
                .SetHeader("X-API-Key", Settings.ApiKey)
                .Post()
                .Build();
            var json = searchRequest.ToJson();
            request.Headers.ContentType = "application/json";
            request.SetContent(json);
            request.ContentSummary = json;
            return request;
        }
    }
}
