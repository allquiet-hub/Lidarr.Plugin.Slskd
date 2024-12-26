using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients.Slskd;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.Slskd
{
    public class SlskdParser : IParseIndexerResponse
    {
        private static readonly int[] _bitrates = new[] { 1, 3, 9 };

        public SlskdUser User { get; set; }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse response)
        {
            var torrentInfos = new List<ReleaseInfo>();

            var jsonResponse = new HttpResponse<SlskdSearchResponse>(response.HttpResponse);

            foreach (var result in jsonResponse.Resource.Data)
            {
                // MP3 128
                torrentInfos.Add(ToReleaseInfo(result, 1));

                // MP3 320
                if (User.CanStreamHq)
                {
                    torrentInfos.Add(ToReleaseInfo(result, 3));
                }

                // FLAC
                if (User.CanStreamHq)
                {
                    torrentInfos.Add(ToReleaseInfo(result, 9));
                }
            }

            // order by date
            return
                torrentInfos
                    .OrderByDescending(o => o.Size)
                    .ToArray();
        }

        private static ReleaseInfo ToReleaseInfo(SlskdGwAlbum x, int bitrate)
        {
            var publishDate = DateTime.UtcNow;
            var year = 0;
            if (DateTime.TryParse(x.DigitalReleaseDate, out var digitalReleaseDate))
            {
                publishDate = digitalReleaseDate;
                year = publishDate.Year;
            }
            else if (DateTime.TryParse(x.PhysicalReleaseDate, out var physicalReleaseDate))
            {
                publishDate = physicalReleaseDate;
                year = publishDate.Year;
            }

            var result = new ReleaseInfo
            {
                Guid = $"Slskd-{x.AlbumId}-{bitrate}",
                Artist = x.ArtistName,
                Album = x.AlbumTitle,
                DownloadUrl = x.Link,
                InfoUrl = x.Link,
                PublishDate = publishDate,
                DownloadProtocol = nameof(SlskdDownloadProtocol)
            };

            long actualBitrate;
            string format;
            switch (bitrate)
            {
                case 9:
                    actualBitrate = 1000;
                    result.Codec = "FLAC";
                    result.Container = "Lossless";
                    format = "FLAC";
                    break;
                case 3:
                    actualBitrate = 320;
                    result.Codec = "MP3";
                    result.Container = "320";
                    format = "MP3 320";
                    break;
                case 1:
                    actualBitrate = 128;
                    result.Codec = "MP3";
                    result.Container = "128";
                    format = "MP3 128";
                    break;
                default:
                    throw new NotImplementedException();
            }

            // bitrate is in kbit/sec, 128 = 1024/8
            result.Size = x.DurationInSeconds * actualBitrate * 128L;
            result.Title = $"{x.ArtistName} - {x.AlbumTitle}";

            if (year > 0)
            {
                result.Title += $" ({year})";
            }

            if (x.Explicit)
            {
                result.Title += " [Explicit]";
            }

            result.Title += $" [{format}] [WEB]";

            return result;
        }
    }
}
