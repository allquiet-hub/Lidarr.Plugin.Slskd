using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Blocklisting
{
    public class SlskdBlocklist : IBlocklistForProtocol
    {
        private readonly IBlocklistRepository _blocklistRepository;

        public SlskdBlocklist(IBlocklistRepository blocklistRepository)
        {
            _blocklistRepository = blocklistRepository;
        }

        public string Protocol => nameof(SlskdDownloadProtocol);

        public bool IsBlocklisted(int artistId, ReleaseInfo release)
        {
            var blocklistedByTorrentInfohash = _blocklistRepository.BlocklistedByTorrentInfoHash(artistId, release.Guid);
            return blocklistedByTorrentInfohash.Any(b => SameRelease(b, release));
        }

        public Blocklist GetBlocklist(DownloadFailedEvent message)
        {
            return new Blocklist
            {
                ArtistId = message.ArtistId,
                AlbumIds = message.AlbumIds,
                SourceTitle = message.SourceTitle,
                Quality = message.Quality,
                Date = DateTime.UtcNow,
                PublishedDate = DateTime.TryParse(message.Data.GetValueOrDefault("publishedDate"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var publishedDate)
                    ? publishedDate
                    : DateTime.UtcNow,
                Size = long.TryParse(message.Data.GetValueOrDefault("size", "0"), out var size) ? size : 0,
                Indexer = message.Data.GetValueOrDefault("indexer"),
                Protocol = message.Data.GetValueOrDefault("protocol"),
                Message = message.Message,
                TorrentInfoHash = message.Data.GetValueOrDefault("guid")
            };
        }

        private bool SameRelease(Blocklist item, ReleaseInfo release)
        {
            if (release.Guid.IsNotNullOrWhiteSpace())
            {
                return release.Guid.Equals(item.TorrentInfoHash);
            }

            return item.Indexer.Equals(release.Indexer, StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
