using System.Collections.Generic;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;

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

            var url = $"{Settings.BaseUrl.TrimEnd('/')}/api/newReleases";

            pageableRequests.Add(new[]
            {
                new IndexerRequest(url, HttpAccept.Json)
            });

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            chain.AddTier(GetRequests($"artist:\"{searchCriteria.ArtistQuery}\" album:\"{searchCriteria.AlbumQuery}\""));
            chain.AddTier(GetRequests($"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}"));

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            chain.AddTier(GetRequests($"artist:\"{searchCriteria.ArtistQuery}\""));
            chain.AddTier(GetRequests(searchCriteria.ArtistQuery));

            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters)
        {
            for (var page = 0; page < MAX_PAGES; page++)
            {
                var url = $"{Settings.BaseUrl.TrimEnd('/')}/api/album-search?term={searchParameters}&nb={PAGE_SIZE}&start={page * PAGE_SIZE}";
                yield return new IndexerRequest(url, HttpAccept.Json);
            }
        }
    }
}
