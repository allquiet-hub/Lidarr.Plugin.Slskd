using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Core.Indexers.Slskd
{
    public sealed class SlskdRequestGenerator : IIndexerRequestGenerator
    {
        private const int PAGE_SIZE = 100;
        private const int MAX_PAGES = 30;
        public SlskdIndexerSettings Settings { get; set; }
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
                chain.AddTier(GetRequests($"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}", searchCriteria.Tracks?.Count, 5));
                chain.AddTier(GetRequests($"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}", searchCriteria.Tracks?.Count, 20));
                chain.AddTier(GetRequests($"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}", searchCriteria.Tracks?.Count, 60));
            }

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters, int? numberOfTracks = null, int? searchTimeout = null)
        {
            if (numberOfTracks is null or 0)
            {
                numberOfTracks = 1;
            }

            var searchRequest = new SearchRequest(searchParameters, numberOfTracks)
            {
                //Seconds to milliseconds
                SearchTimeout = searchTimeout == null ? Settings.SearchTimeout * 1000 : searchTimeout.Value * 1000,

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
            var json = JsonConvert.SerializeObject(searchRequest);
            request.Headers.ContentType = "application/json";
            request.SetContent(json);
            request.ContentSummary = json;
            return request;
        }
    }
}
