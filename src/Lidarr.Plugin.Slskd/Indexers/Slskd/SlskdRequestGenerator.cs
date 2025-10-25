using System;
using System.Collections.Generic;
using System.Globalization;
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
        // Properties first
        public SlskdIndexerSettings Settings { get; init; }

        // Static members
        private static readonly HashSet<string> VariousArtistIds = new (StringComparer.OrdinalIgnoreCase)
        {
            "89ad4ac3-39f7-470e-963a-56509c546377"
        };

        private static readonly HashSet<string> VariousArtistNames = new (StringComparer.OrdinalIgnoreCase)
        {
            "various artists",
            "various",
            "va",
            "unknown"
        };

        private static HttpRequestBuilder CreateRequestBuilder(SlskdIndexerSettings settings) =>
            new HttpRequestBuilder(settings.BaseUrl)
                .Accept(HttpAccept.Json)
                .SetHeader("X-API-Key", settings.ApiKey);

        private static int GetMinimumTrackCount(AlbumSearchCriteria searchCriteria)
        {
            var albumReleases = searchCriteria.Albums.First().AlbumReleases;
            return albumReleases?.Value?.Any() == true
                ? albumReleases.Value.Min(r => r.TrackCount)
                : 0;
        }

        private static bool IsVariousArtist(Core.Music.Artist artist) =>
            VariousArtistIds.Contains(artist.ForeignArtistId) ||
            VariousArtistNames.Contains(artist.Name);

        // Instance members after
        private readonly Logger _logger;
        private readonly HttpRequestBuilder _requestBuilder;

        public SlskdRequestGenerator(Logger logger, SlskdIndexerSettings settings)
        {
            _logger = logger;
            Settings = settings;
            _requestBuilder = CreateRequestBuilder(settings);
        }

        public IndexerPageableRequestChain GetRecentRequests()
        {
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetRequests("Silent Partner Chances", searchTimeout: 5000));
            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            if (searchCriteria == null)
            {
                throw new ArgumentNullException(nameof(searchCriteria));
            }

            _logger.Debug("Creating search request for album: {0}", searchCriteria.AlbumQuery);

            var chain = new IndexerPageableRequestChain();

            var minimumTrackCount = GetMinimumTrackCount(searchCriteria);
            _logger.Debug("Minimum track count: {0}", minimumTrackCount);

            var isVariousArtist = IsVariousArtist(searchCriteria.Artist);
            _logger.Debug("Is various artist: {0}", isVariousArtist);

            if (!isVariousArtist)
            {
                _logger.Debug("Searching for artist: {0}", searchCriteria.ArtistQuery);
                AddArtistSearches(chain, searchCriteria, minimumTrackCount);
            }
            else
            {
                _logger.Debug("Searching for various artists, ignoring artist name, skip to searching by album only");
            }

            _logger.Debug("Adding album-only searches for: {0}", searchCriteria.AlbumQuery);
            AddAlbumOnlySearches(chain, searchCriteria, minimumTrackCount);

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            return new IndexerPageableRequestChain();
        }

        private void AddArtistSearches(IndexerPageableRequestChain chain, AlbumSearchCriteria searchCriteria, int minimumTrackCount)
        {
            // Primary artist search
            AddSearchRequests(chain,
                $"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}",
                minimumTrackCount);

            // Alias searches
            var artistMetadata = searchCriteria.Artist.Metadata.Value;
            foreach (var alias in artistMetadata.Aliases)
            {
                AddSearchRequests(chain, $"{alias} {searchCriteria.AlbumQuery}", minimumTrackCount);
            }
        }

        private void AddAlbumOnlySearches(IndexerPageableRequestChain chain, AlbumSearchCriteria searchCriteria, int minimumTrackCount)
        {
            AddSearchRequests(chain, searchCriteria.CleanAlbumQuery, minimumTrackCount);
            AddSearchRequests(chain, searchCriteria.AlbumQuery, minimumTrackCount);
        }

        private void AddSearchRequests(IndexerPageableRequestChain chain, string query, int trackCount)
        {
            if (trackCount > 0 && Settings.SearchResultsWithLessFilesThanAlbumFirst)
            {
                _logger.Debug("Adding search with track count filter: {0} tracks for query: {1}", trackCount, query);
                chain.AddTier(GetRequests(query, trackCount: trackCount));
            }

            _logger.Debug("Adding search without track count filter for query: {0}", query);
            chain.AddTier(GetRequests(query));
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters, int? searchTimeout = null, int? uploadSpeed = null, int trackCount = 0)
        {
            _logger.Debug(CultureInfo.InvariantCulture,
                "Creating search request - Parameters: {0}, Timeout: {1}, Upload Speed: {2}, Track Count: {3}",
                searchParameters,
                searchTimeout,
                uploadSpeed,
                trackCount);

            var searchRequest = CreateSearchRequest(
                searchParameters,
                searchTimeout ?? Settings.SearchTimeout * 1000,
                uploadSpeed ?? Settings.MinimumPeerUploadSpeed,
                trackCount);

            var request = BuildSearchRequest(searchRequest);
            yield return new IndexerRequest(request);
        }

        private static SearchRequest CreateSearchRequest(string searchText, int searchTimeout, int uploadSpeed, int trackCount)
        {
            var request = new SearchRequest
            {
                SearchText = searchText,
                SearchTimeout = searchTimeout,
                FilterResponses = true
            };

            if (uploadSpeed > 0)
            {
                request.MinimumPeerUploadSpeed = uploadSpeed * 1024 * 1024; // Convert MB/s to B/s
            }

            if (trackCount > 0)
            {
                request.MinimumResponseFileCount = trackCount;
            }

            return request;
        }

        private HttpRequest BuildSearchRequest(SearchRequest searchRequest)
        {
            var json = searchRequest.ToJson();
            var request = _requestBuilder
                .Resource("/api/v0/searches/")
                .Post()
                .Build();

            request.Headers.ContentType = "application/json";
            request.SetContent(json);
            request.ContentSummary = json;

            return request;
        }
    }
}
