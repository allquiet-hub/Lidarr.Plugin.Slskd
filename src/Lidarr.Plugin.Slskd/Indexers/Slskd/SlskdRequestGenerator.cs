using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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
        /// <summary>
        /// Carries the album's expected track count from the request generator to the parser.
        /// </summary>
        public const string ExpectedTrackCountHeader = "X-Lidarr-Expected-Tracks";

        /// <summary>
        /// slskd's searchTimeout is an inactivity window, not a total duration: a search ends this long
        /// after the last response arrives, so the wall clock cost is unbounded when results trickle in.
        /// A short window collects the same responses in far less time, since peers that answer at all
        /// answer in bursts; the overall budget is enforced by the parser instead.
        /// </summary>
        private const int InactivityWindowMs = 3000;

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

        private static readonly Regex QualifierPattern = new (@"[\(\[\{][^\)\]\}]*[\)\]\}]", RegexOptions.Compiled);
        private static readonly Regex PunctuationPattern = new (@"[^\p{L}\p{Nd}\s]", RegexOptions.Compiled);
        private static readonly Regex WhitespacePattern = new (@"\s+", RegexOptions.Compiled);

        private static HttpRequestBuilder CreateRequestBuilder(SlskdIndexerSettings settings) =>
            new HttpRequestBuilder(settings.BaseUrl)
                .Accept(HttpAccept.Json)
                .SetHeader("X-API-Key", settings.ApiKey);

        private static int GetMinimumTrackCount(AlbumSearchCriteria searchCriteria)
        {
            var albumReleases = searchCriteria.Albums.FirstOrDefault()?.AlbumReleases;
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

            // Every tier is a full slskd search that has to run to completion, so the chain is kept as
            // short as possible: Lidarr stops at the first tier that yields anything.
            foreach (var query in BuildQueries(searchCriteria))
            {
                _logger.Debug("Adding search tier for query: {0}", query);
                chain.AddTier(GetRequests(query, trackCount: minimumTrackCount));
            }

            return chain;
        }

        /// <summary>
        /// Builds the search queries in decreasing order of expected recall.
        ///
        /// Soulseek requires every term of a query to be present in a result, so qualifiers such as
        /// '(Remixes)' or '[Deluxe Edition]' shrink the result set by an order of magnitude. They are
        /// dropped rather than tried first: the broad query still returns the qualified folders, and
        /// Lidarr decides whether they match on the parsed title.
        /// </summary>
        private IEnumerable<string> BuildQueries(AlbumSearchCriteria searchCriteria)
        {
            var album = Simplify(searchCriteria.AlbumQuery);
            if (album.IsNullOrWhiteSpace())
            {
                album = Simplify(searchCriteria.CleanAlbumQuery);
            }

            if (album.IsNullOrWhiteSpace())
            {
                yield break;
            }

            var candidates = new List<string>();
            var isVariousArtist = IsVariousArtist(searchCriteria.Artist);

            if (!isVariousArtist)
            {
                candidates.Add($"{Simplify(searchCriteria.ArtistQuery)} {album}");
            }

            candidates.Add(album);

            if (!isVariousArtist)
            {
                foreach (var alias in searchCriteria.Artist.Metadata.Value.Aliases)
                {
                    candidates.Add($"{Simplify(alias)} {album}");
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                var query = WhitespacePattern.Replace(candidate ?? string.Empty, " ").Trim();
                if (query.Length > 0 && seen.Add(query))
                {
                    yield return query;
                }
            }
        }

        /// <summary>
        /// Strips bracketed qualifiers and punctuation so the query stays inside what Soulseek can match.
        /// Apostrophes are removed rather than replaced, so "Don't" stays a single term instead of
        /// becoming "Don t" and requiring a bogus one-letter term to be present.
        /// </summary>
        private static string Simplify(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            var simplified = QualifierPattern.Replace(value, " ");
            simplified = simplified.Replace("'", string.Empty).Replace("’", string.Empty);
            simplified = PunctuationPattern.Replace(simplified, " ");

            return WhitespacePattern.Replace(simplified, " ").Trim();
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            return new IndexerPageableRequestChain();
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
                searchTimeout ?? Math.Min(InactivityWindowMs, Settings.SearchTimeout * 1000),
                uploadSpeed ?? Settings.MinimumPeerUploadSpeed);

            var request = BuildSearchRequest(searchRequest, trackCount);
            yield return new IndexerRequest(request);
        }

        private SearchRequest CreateSearchRequest(string searchText, int searchTimeout, int uploadSpeed)
        {
            // MinimumResponseFileCount is deliberately left unset: filtering incomplete results away
            // server-side costs an extra search tier and hides them from interactive search, so the
            // count is carried to the parser instead and enforced by SlskdCompleteReleaseSpecification.
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

            if (Settings.ResponseLimit > 0)
            {
                // Completes popular searches as soon as enough users answer, instead of always
                // running out the full timeout
                request.ResponseLimit = Settings.ResponseLimit;
            }

            if (Settings.MaximumPeerQueueLength > 0)
            {
                // Skips users whose queue is so long the transfer would never realistically start
                request.MaximumPeerQueueLength = Settings.MaximumPeerQueueLength;
            }

            return request;
        }

        private HttpRequest BuildSearchRequest(SearchRequest searchRequest, int trackCount)
        {
            var json = searchRequest.ToJson();
            var request = _requestBuilder
                .Resource("/api/v0/searches/")
                .Post()
                .Build();

            request.Headers.ContentType = "application/json";
            request.SetContent(json);
            request.ContentSummary = json;

            // Not part of the slskd API: the parser needs the expected track count to flag incomplete
            // releases, and the response carries no way back to the search criteria. slskd ignores it.
            if (trackCount > 0 && !Settings.AllowIncompleteReleases)
            {
                request.Headers.Add(ExpectedTrackCountHeader, trackCount.ToString(CultureInfo.InvariantCulture));
            }

            return request;
        }
    }
}
