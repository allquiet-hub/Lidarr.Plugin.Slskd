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
using NzbDrone.Plugin.Slskd.Helpers;
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
        /// Carry the library's exact artist name and album title to the parser, base64 encoded because
        /// header values must stay ASCII. Both feed the title annotation that keeps releases mappable.
        /// </summary>
        public const string ArtistNameHeader = "X-Lidarr-Artist";

        public const string AlbumTitleHeader = "X-Lidarr-Album";

        /// <summary>
        /// slskd's searchTimeout is an inactivity window, not a total duration: a search ends this long
        /// after the last response arrives, so the wall clock cost is unbounded when results trickle in.
        /// A short window collects the same responses in far less time, since peers that answer at all
        /// answer in bursts; the overall budget is enforced by the parser instead.
        /// </summary>
        private const int InactivityWindowMs = 3000;

        /// <summary>
        /// Words an album title needs before it is worth searching for on its own, without the artist.
        /// </summary>
        private const int DistinctiveAlbumWordCount = 3;

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
                chain.AddTier(GetRequests(
                    query,
                    trackCount: minimumTrackCount,
                    artistName: searchCriteria.Artist?.Name,
                    albumTitle: searchCriteria.Albums?.FirstOrDefault()?.Title));
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
                // Some albums are titled after their artist, and repeating the name only lengthens the
                // query without narrowing it, since every term still has to be present
                var artist = Simplify(searchCriteria.ArtistQuery);
                candidates.Add(album.StartsWith(artist, StringComparison.OrdinalIgnoreCase)
                    ? AsUnit(album)
                    : $"{AsUnit(artist)} {AsUnit(album)}");
            }

            // Dropping the artist widens the search to anything sharing the album's words, which only
            // pays off when those words are specific enough to stand alone. A short title such as
            // "Scrap Metal" matches unrelated folders by the dozen, turning an honest "not found" into a
            // page of noise that Lidarr then has to reject one by one.
            if (WordCount(album) >= DistinctiveAlbumWordCount)
            {
                candidates.Add(AsUnit(album));
            }

            if (!isVariousArtist)
            {
                foreach (var alias in searchCriteria.Artist.Metadata.Value.Aliases)
                {
                    candidates.Add($"{AsUnit(Simplify(alias))} {AsUnit(album)}");
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
        /// <summary>
        /// Binds the words of a single field together with '+', so the artist and the album each act as
        /// one unit instead of dissolving into loose terms.
        ///
        /// Left as separate words, an artist like "DJ Dark" matches anything holding "dj" and "dark"
        /// anywhere in its path, and short common words drag in unrelated folders by the dozen. Bound as
        /// units the same search returns a handful of results without losing the ones that matter.
        /// </summary>
        private static string AsUnit(string value) =>
            value.IsNullOrWhiteSpace() ? value : value.Replace(' ', '+');

        private static int WordCount(string value) =>
            value.IsNullOrWhiteSpace() ? 0 : value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

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

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters, int? searchTimeout = null, double? uploadSpeed = null, int trackCount = 0, string artistName = null, string albumTitle = null)
        {
            _logger.Debug(CultureInfo.InvariantCulture,
                "Creating search request - Parameters: {0}, Timeout: {1}, Upload Speed: {2}, Track Count: {3}",
                searchParameters,
                searchTimeout,
                uploadSpeed,
                trackCount);

            var searchRequest = CreateSearchRequest(
                searchParameters,
                searchTimeout ?? InactivityWindowMs,
                uploadSpeed ?? Settings.MinimumPeerUploadSpeed);

            var request = BuildSearchRequest(searchRequest, trackCount, artistName, albumTitle);
            yield return new IndexerRequest(request);
        }

        private SearchRequest CreateSearchRequest(string searchText, int searchTimeout, double uploadSpeed)
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
                request.MinimumPeerUploadSpeed = (int)Math.Round(uploadSpeed * 1024 * 1024); // Convert MB/s to B/s
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

        private HttpRequest BuildSearchRequest(SearchRequest searchRequest, int trackCount, string artistName, string albumTitle)
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

            if (artistName.IsNotNullOrWhiteSpace())
            {
                request.Headers.Add(ArtistNameHeader, FileProcessingUtils.Base64Encode(artistName));
            }

            if (albumTitle.IsNotNullOrWhiteSpace())
            {
                request.Headers.Add(AlbumTitleHeader, FileProcessingUtils.Base64Encode(albumTitle));
            }

            return request;
        }
    }
}
