using System;
using System.Collections.Generic;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers.Slskd
{
    /// <summary>
    /// A search that knows which albums it is looking for, so that a fetch covering several albums can
    /// stop falling back for each of them separately instead of for all of them at once.
    ///
    /// More than one album when two share a query, as an album and a reissue under the same title do:
    /// the search runs once and counts for both. Empty for a search of a single album, whose fetch
    /// stops at the first tier that finds anything, like any other indexer's.
    /// </summary>
    public class SlskdIndexerRequest : IndexerRequest
    {
        public SlskdIndexerRequest(HttpRequest httpRequest, IReadOnlyCollection<int> albumIds, IReadOnlyList<ArtistSearchAlbum> artistAlbums)
            : base(httpRequest)
        {
            AlbumIds = albumIds ?? Array.Empty<int>();
            ArtistAlbums = artistAlbums ?? Array.Empty<ArtistSearchAlbum>();
        }

        public IReadOnlyCollection<int> AlbumIds { get; }

        /// <summary>
        /// Every monitored album of the artist being searched, which is the set Lidarr maps each release
        /// of an artist search against. Empty outside an artist search.
        /// </summary>
        public IReadOnlyList<ArtistSearchAlbum> ArtistAlbums { get; }
    }

    /// <summary>
    /// What the parser needs to know about an album a release of an artist search may belong to: the
    /// names that make its title map, and the track counts its completeness is judged by.
    /// </summary>
    public class ArtistSearchAlbum
    {
        public ArtistSearchAlbum(string title, int year, int minimumTrackCount, int maximumTrackCount)
        {
            Title = title;
            Year = year;
            MinimumTrackCount = minimumTrackCount;
            MaximumTrackCount = maximumTrackCount;
        }

        public string Title { get; }
        public int Year { get; }
        public int MinimumTrackCount { get; }
        public int MaximumTrackCount { get; }
    }
}
