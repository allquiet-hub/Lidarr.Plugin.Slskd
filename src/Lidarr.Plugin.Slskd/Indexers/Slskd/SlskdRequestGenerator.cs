using System.Collections.Generic;
using System.Linq;
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
            pageableRequests.Add(GetRequests("Silent Partner Chances", searchTimeout: 5000, 0));
            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            var albumsMinimumTrackCount = searchCriteria.Albums.First().AlbumReleases.Value.OrderBy(r => r.TrackCount).First().TrackCount;
            var artistMetadata = searchCriteria.Artist.Metadata.Value;
            chain.AddTier(GetRequests($"{searchCriteria.ArtistQuery} {searchCriteria.CleanAlbumQuery}", trackCount: albumsMinimumTrackCount));
            foreach (var alias in artistMetadata.Aliases)
            {
                chain.AddTier(GetRequests($"{alias} {searchCriteria.CleanAlbumQuery}", trackCount: albumsMinimumTrackCount));
            }

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            return new IndexerPageableRequestChain();
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters, int? searchTimeout = null, int? uploadSpeed = null, int trackCount = 0)
        {
            searchTimeout ??= Settings.SearchTimeout * 1000; // Default to settings timeout
            uploadSpeed ??= Settings.MinimumPeerUploadSpeed;
            var searchRequest = new SearchRequest()
            {
                SearchText = searchParameters,
                SearchTimeout = searchTimeout.Value,
            };

            if (uploadSpeed > 0)
            {
                searchRequest.MinimumPeerUploadSpeed = uploadSpeed * 1024 * 1024; // Convert MB/s to B/s
            }

            if (Settings.IgnoreResultsWithLessFilesThanAlbum && trackCount > 0)
            {
                searchRequest.MinimumResponseFileCount = trackCount;
            }

            searchRequest.FilterResponses = true;

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
