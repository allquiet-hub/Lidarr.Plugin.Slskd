using System.Collections.Generic;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Core.Indexers.Slskd
{
    public class SlskdRequestGenerator : IIndexerRequestGenerator
    {
        private const int PAGE_SIZE = 100;
        private const int MAX_PAGES = 30;
        public SlskdIndexerSettings Settings { get; set; }
        public Logger Logger { get; set; }

        public virtual IndexerPageableRequestChain GetRecentRequests()
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
                chain.AddTier(GetRequests($"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}", searchCriteria.Tracks?.Count));
            }

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            if (searchCriteria != null)
            {
                chain.AddTier(GetRequests(searchCriteria.ArtistQuery, searchCriteria.Tracks.Count));
            }

            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters, int? numberOfTracks = null, int? searchTimeout = null)
        {
            if (numberOfTracks is null or 0)
            {
                numberOfTracks = 1;
            }

            var searchRequest = new SearchRequest(searchParameters, numberOfTracks);
            if (searchTimeout == null)
            {
                //Seconds to milliseconds
                searchRequest.SearchTimeout = Settings.SearchTimeout * 1000;
            }
            else
            {
                searchRequest.SearchTimeout = searchTimeout.Value * 1000;
            }

            //MB/s to B/s
            searchRequest.MinimumPeerUploadSpeed = Settings.MinimumPeerUploadSpeed * 1024 * 1024;
            var json = JsonConvert.SerializeObject(searchRequest);
            var request = RequestBuilder()
                .Resource($"api/v0/searches")
                .Post()
                .Build();

            request.SetContent(json);
            request.ContentSummary = json;
            request.Headers.ContentType = "application/json";

            yield return new IndexerRequest(request);
        }

        private HttpRequestBuilder RequestBuilder()
        {
            return new HttpRequestBuilder(Settings.BaseUrl)
                .Accept(HttpAccept.Json)
                .SetHeader("X-API-Key", Settings.ApiKey);
        }
    }
}
