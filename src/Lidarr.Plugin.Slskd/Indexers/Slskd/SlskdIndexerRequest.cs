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
        public SlskdIndexerRequest(HttpRequest httpRequest, IReadOnlyCollection<int> albumIds)
            : base(httpRequest)
        {
            AlbumIds = albumIds ?? Array.Empty<int>();
        }

        public IReadOnlyCollection<int> AlbumIds { get; }
    }
}
