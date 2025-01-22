using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
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
        private static readonly List<string> VariousArtistIds = new () { "89ad4ac3-39f7-470e-963a-56509c546377" };
        private static readonly List<string> VariousArtistNames = new () { "various artists", "various", "va", "unknown" };

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
            var albumReleases = searchCriteria.Albums.First().AlbumReleases;
            var albumsMinimumTrackCount = albumReleases?.Value?.Any() == true
                ? albumReleases.Value.OrderBy(r => r.TrackCount).First().TrackCount
                : 0;

            var artistMetadata = searchCriteria.Artist.Metadata.Value;
            var isVariousArtist = VariousArtistIds.ContainsIgnoreCase(searchCriteria.Artist.ForeignArtistId) ||
                                  VariousArtistNames.ContainsIgnoreCase(searchCriteria.Artist.Name);

            if (isVariousArtist)
            {
                Logger.Debug("Searching for various artists, ignoring artist name, skip to searching by album only");
            }
            else
            {
                AddSearchRequests(chain, $"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}", albumsMinimumTrackCount);
                foreach (var alias in artistMetadata.Aliases)
                {
                    AddSearchRequests(chain, $"{alias} {searchCriteria.AlbumQuery}", albumsMinimumTrackCount);
                }
            }

            AddSearchRequests(chain, $"{searchCriteria.CleanAlbumQuery}", albumsMinimumTrackCount);
            AddSearchRequests(chain, $"{searchCriteria.AlbumQuery}", albumsMinimumTrackCount);

            return chain;
        }

        // Helper Method to Reduce Repetition
        private void AddSearchRequests(IndexerPageableRequestChain chain, string query, int trackCount)
        {
            if (trackCount > 0 && Settings.SearchResultsWithLessFilesThanAlbumFirst)
            {
                chain.AddTier(GetRequests(query, trackCount: trackCount));
            }

            chain.AddTier(GetRequests(query));
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

            if (trackCount > 0)
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
