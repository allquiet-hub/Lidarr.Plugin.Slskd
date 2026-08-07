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
        /// The largest track count among the album releases the import could settle on, used to reject
        /// folders holding more audio than any of them could absorb.
        /// </summary>
        public const string MaximumTrackCountHeader = "X-Lidarr-Expected-Tracks-Max";

        /// <summary>
        /// Carry the library's exact artist name and album title to the parser, base64 encoded because
        /// header values must stay ASCII. Both feed the title annotation that keeps releases mappable.
        /// </summary>
        public const string ArtistNameHeader = "X-Lidarr-Artist";

        public const string AlbumTitleHeader = "X-Lidarr-Album";

        /// <summary>
        /// The album's release year, which the parser needs to build a title for artists whose name
        /// cannot survive Lidarr's criteria matching (see EnsureMappableTitle).
        /// </summary>
        public const string AlbumYearHeader = "X-Lidarr-Album-Year";

        /// <summary>
        /// slskd's searchTimeout is an inactivity window, not a total duration: a search ends this long
        /// after the last response arrives, so the wall clock cost is unbounded when results trickle in.
        /// A short window collects the same responses in far less time, since peers that answer at all
        /// answer in bursts; the overall budget is enforced by the parser instead.
        /// </summary>
        private const int InactivityWindowMs = 3000;

        /// <summary>
        /// Set high enough that it stops binding, leaving slskd's own cap of ~25000 files to end a
        /// search instead. A limit that binds first keeps whoever answers fastest, which on a popular
        /// query is dominated by folders of a better known album sharing the search terms, and hides
        /// a third to a half of what is on offer - the album actually being searched for among it.
        /// Rare searches never reach any limit.
        /// </summary>
        private const int ResponseLimit = 5000;

        /// <summary>
        /// Words an album title needs before it is worth searching for on its own, without the artist.
        /// A title of two words is specific enough that the folders sharing them are overwhelmingly the
        /// album itself, while a single common word is not: searching one returns thousands of folders
        /// with nothing to do with the record. Distinctiveness, not length, is what decides that, but
        /// nothing tells them apart before the search, and the word count is the closest stand-in.
        /// </summary>
        private const int DistinctiveAlbumWordCount = 2;

        /// <summary>
        /// Shortest alias worth a query of its own. Initials and abbreviations match everything.
        /// </summary>
        private const int MinimumAliasLength = 4;

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
        private static readonly Regex WhitespacePattern = new (@"\s+", RegexOptions.Compiled);
        private static readonly Regex LeadingDashPattern = new (@"(?<![^\s])-+(?=\S)", RegexOptions.Compiled);

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

        /// <summary>
        /// The largest track count among the releases the import is allowed to map against — the
        /// monitored one, or any of them when the album accepts any release. A folder with more audio
        /// files than this cannot import cleanly: the surplus files map to nothing.
        /// </summary>
        private static int GetMaximumTrackCount(AlbumSearchCriteria searchCriteria)
        {
            var album = searchCriteria.Albums.FirstOrDefault();
            var releases = album?.AlbumReleases?.Value;
            if (releases == null || !releases.Any())
            {
                return 0;
            }

            var eligible = releases.Where(r => r.Monitored || album.AnyReleaseOk).ToList();
            return (eligible.Any() ? eligible : releases).Max(r => r.TrackCount);
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

        /// <summary>
        /// Empty because Soulseek has nothing resembling a feed of recent uploads. The indexer declares
        /// no RSS support, so nothing asks for these, and the connection test queries slskd's own state
        /// rather than issuing a query that would have to be invented here.
        /// </summary>
        public IndexerPageableRequestChain GetRecentRequests()
        {
            return new IndexerPageableRequestChain();
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
            var maximumTrackCount = GetMaximumTrackCount(searchCriteria);

            // Every tier is a full slskd search that has to run to completion, so the chain is kept as
            // short as possible: Lidarr stops at the first tier that yields anything. Queries that
            // belong together run inside one tier so neither can starve the other of the chance to run,
            // and each must be added as its own pageable request: chained into one enumerable they
            // read as pages of a single query, and pagination stops after the first short page, which
            // silently drops every query after the first. Duplicated folders across the queries are
            // collapsed by Lidarr on the release Guid.
            foreach (var tier in BuildQueryTiers(searchCriteria))
            {
                _logger.Debug("Adding search tier for queries: {0}", string.Join(" | ", tier));

                var first = true;
                foreach (var query in tier)
                {
                    var requests = GetRequests(
                        query,
                        trackCount: minimumTrackCount,
                        maximumTrackCount: maximumTrackCount,
                        artistName: searchCriteria.Artist?.Name,
                        albumTitle: searchCriteria.Albums?.FirstOrDefault()?.Title,
                        albumYear: searchCriteria.Albums?.FirstOrDefault()?.ReleaseDate?.Year ?? 0);

                    if (first)
                    {
                        chain.AddTier(requests);
                        first = false;
                    }
                    else
                    {
                        chain.Add(requests);
                    }
                }
            }

            return chain;
        }

        /// <summary>
        /// Builds the search tiers in decreasing order of expected recall, all through one rule: the
        /// query is the raw text a person would type, artist and album with their own spelling,
        /// whitespace collapsed and nothing else touched.
        ///
        /// Raw text matches how the network searches. A query travels as a list of terms split on
        /// spaces, and each peer requires every term somewhere in the file's path: clients differ only
        /// in whether punctuation separates tokens or is matched literally. Leaving it in place
        /// therefore costs nothing and keeps the folders that spell a title the way the artist does,
        /// such as "Don't", which a stripped query misses. Nothing can ask for the album's words to be
        /// adjacent rather than scattered along the path: no client honours quoting, and binding words
        /// with '+' only loses the peers that match punctuation literally without gaining a folder
        /// anywhere else. Terms act as a set, so the repeated words of an album titled after its
        /// artist cost nothing.
        ///
        /// The one transform left is dropping bracketed qualifiers, kept as a sibling query in the
        /// same tier: for a title like "Told You So (Remixes Vol. 1)" the qualified query finds the
        /// right folders while the broad one finds the base album's, and either alone loses half.
        /// </summary>
        private IEnumerable<IReadOnlyList<string>> BuildQueryTiers(AlbumSearchCriteria searchCriteria)
        {
            var album = CollapseWhitespace(searchCriteria.AlbumQuery.IsNullOrWhiteSpace()
                ? searchCriteria.CleanAlbumQuery
                : searchCriteria.AlbumQuery);

            if (album.Length == 0)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var isVariousArtist = IsVariousArtist(searchCriteria.Artist);

            if (!isVariousArtist)
            {
                var artist = CollapseWhitespace(searchCriteria.ArtistQuery);
                var tier = new List<string> { Normalize($"{artist} {album}", seen) };

                var broadAlbum = CollapseWhitespace(QualifierPattern.Replace(album, " "));
                if (broadAlbum.Length > 0 && !broadAlbum.Equals(album, StringComparison.OrdinalIgnoreCase))
                {
                    tier.Add(Normalize($"{artist} {broadAlbum}", seen));
                }

                var filtered = tier.Where(q => q != null).ToList();
                if (filtered.Count > 0)
                {
                    yield return filtered;
                }
            }

            // Dropping the artist widens the search to anything sharing the album's words, which only
            // pays off when those words are specific enough to stand alone. A short title such as
            // "Scrap Metal" matches unrelated folders by the dozen, turning an honest "not found" into a
            // page of noise that Lidarr then has to reject one by one.
            if (WordCount(album) >= DistinctiveAlbumWordCount)
            {
                var query = Normalize(album, seen);
                if (query != null)
                {
                    yield return new[] { query };
                }
            }

            // One alias, not all of them. An artist known internationally carries a name per writing
            // system, and searching each one costs a query for an audience the Soulseek network does
            // not have: a query in a script nobody names their folders in returns nothing, while the
            // count of searches is what the server bans an account over.
            if (!isVariousArtist)
            {
                var alias = searchCriteria.Artist.Metadata.Value.Aliases
                    .FirstOrDefault(a => !a.IsNullOrWhiteSpace() &&
                                         a.Trim().Length >= MinimumAliasLength &&
                                         !a.Equals(searchCriteria.Artist.Name, StringComparison.OrdinalIgnoreCase));

                var query = alias == null ? null : Normalize($"{CollapseWhitespace(alias)} {album}", seen);
                if (query != null)
                {
                    yield return new[] { query };
                }
            }
        }

        private static string CollapseWhitespace(string value) =>
            value.IsNullOrWhiteSpace() ? string.Empty : WhitespacePattern.Replace(value, " ").Trim();

        /// <summary>
        /// Drops the dash a term starts with, which the search API reads as an exclusion: a title like
        /// "-Ology" would otherwise turn its own word into a NOT clause and search for everything but
        /// the album. A dash standing on its own is left alone, being a separator rather than a term.
        /// </summary>
        private static string Normalize(string candidate, HashSet<string> seen)
        {
            var query = CollapseWhitespace(LeadingDashPattern.Replace(candidate, string.Empty));
            return query.Length > 0 && seen.Add(query) ? query : null;
        }

        private static int WordCount(string value) =>
            value.IsNullOrWhiteSpace() ? 0 : value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            return new IndexerPageableRequestChain();
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters, int? searchTimeout = null, double? uploadSpeed = null, int trackCount = 0, int maximumTrackCount = 0, string artistName = null, string albumTitle = null, int albumYear = 0)
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

            var request = BuildSearchRequest(searchRequest, trackCount, maximumTrackCount, artistName, albumTitle, albumYear);
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

            // Fixed rather than configurable, calibrated by measurement: popular searches stop on
            // their own well before this (slskd caps a search at ~25000 files), mid searches have
            // populations in the hundreds that a lower limit would truncate — cutting out exactly the
            // peers being searched for — and rare searches never reach any limit. Every value above
            // the mid-search population behaves identically, so there is no trade-off to expose.
            request.ResponseLimit = ResponseLimit;

            if (Settings.MaximumPeerQueueLength > 0)
            {
                // Skips users whose queue is so long the transfer would never realistically start
                request.MaximumPeerQueueLength = Settings.MaximumPeerQueueLength;
            }

            return request;
        }

        private HttpRequest BuildSearchRequest(SearchRequest searchRequest, int trackCount, int maximumTrackCount, string artistName, string albumTitle, int albumYear)
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

            if (albumYear > 0)
            {
                request.Headers.Add(AlbumYearHeader, albumYear.ToString(CultureInfo.InvariantCulture));
            }

            if (maximumTrackCount > 0)
            {
                request.Headers.Add(MaximumTrackCountHeader, maximumTrackCount.ToString(CultureInfo.InvariantCulture));
            }

            return request;
        }
    }
}
