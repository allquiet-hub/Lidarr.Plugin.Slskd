using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Validation;
using NzbDrone.Plugin.Slskd.Helpers;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Core.Download.Clients.Slskd
{
    public class Slskd : DownloadClientBase<SlskdSettings>
    {
        private readonly ISlskdProxy _proxy;

        public Slskd(ISlskdProxy proxy,
                      IConfigService configService,
                      IDiskProvider diskProvider,
                      IRemotePathMappingService remotePathMappingService,
                      ILocalizationService localizationService,
                      Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _proxy = proxy;
        }

        public override string Protocol => nameof(SlskdDownloadProtocol);

        public override string Name => "Slskd";

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var queue = _proxy.GetQueue(Settings);

            foreach (var item in queue)
            {
                item.DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false);
                item.OutputPath = _remotePathMappingService.RemapRemoteToLocal(Settings.Host, item.OutputPath);
            }

            return queue;
        }

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            _proxy.RemoveFromQueue(item.DownloadId, deleteData, Settings);
        }

        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
        {
            var release = remoteAlbum.Release;

            try
            {
                // The release identifier travels with the release instead of being recomputed from the
                // download path: that path is the folder for albums but the file itself for single-file
                // releases, so deriving it a second time here would not always agree with what the queue
                // reports. The completed folder is named "Artist - Album" because that name does double
                // duty: Lidarr identifies what it imported largely from it, and the queue maps a download
                // back to its album only by parsing a title, never by the ids already sitting in history.
                // A parsed title resolves through normalised lookups that forgive punctuation, while the
                // history fallback demands the library's exact punctuation inside a stranger's folder name.
                var album = remoteAlbum.Albums?.FirstOrDefault();
                var artistName = remoteAlbum.Artist?.Name ?? album?.Artist?.Value?.Name;
                var folderName = album?.Title == null || artistName == null
                    ? album?.Title
                    : $"{artistName} - {album.Title}";

                // What Lidarr tracks is the grab, not the folder, so the album has to be part of the
                // identity: the same folder is offered for the album and for every single whose track it
                // contains, and one identifier across those grabs merges them into a single tracked
                // download that no import can satisfy. A grab carrying no album leaves nothing finer to
                // key on than the release itself.
                var downloadId = album == null
                    ? release.Guid
                    : ReleaseIdentifier.ForDownload(release.Guid, album.Id);

                return Task.FromResult(_proxy.Download(release.Origin, release.Source, release.DownloadUrl, downloadId, folderName, Settings));
            }
            catch (SlskdPeerUnavailableException ex)
            {
                // Reported as unavailable rather than as a download failure, so Lidarr tries the next
                // release without holding the indexer responsible for an unreachable peer
                throw new ReleaseUnavailableException(release, ex.Message, ex);
            }
            catch (SlskdEnqueueRefusedException ex)
            {
                // Rejected rather than grabbed: nothing would ever arrive under this download, and a
                // grab Lidarr believed in would sit in history without a queue item to follow
                throw new DownloadClientRejectedReleaseException(release, ex.Message, ex);
            }
        }

        public override DownloadClientInfo GetStatus()
        {
            var config = _proxy.GetOptions(Settings);

            return new DownloadClientInfo
            {
                IsLocalhost = Settings.Host is "127.0.0.1" or "localhost",
                OutputRootFolders = new List<OsPath> { _remotePathMappingService.RemapRemoteToLocal(Settings.Host, new OsPath(config.Directories.Downloads)) }
            };
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestSettings());
        }

        private ValidationFailure TestSettings()
        {
            var config = _proxy.GetOptions(Settings);

            if (config is null)
            {
                return new NzbDroneValidationFailure(string.Empty, "Could not connect to Slskd")
                {
                    InfoLink = HttpRequestBuilder.BuildBaseUrl(Settings.UseSsl, Settings.Host, Settings.Port, Settings.UrlBase),
                    DetailedDescription = "Could not connect to Slskd, please check your settings",
                };
            }

            var application = _proxy.GetApplication(Settings);

            var version = TestVersion(application);
            if (version != null)
            {
                return version;
            }

            // Reaching this far means the URL and API key work, so a failure here is not theirs to fix:
            // slskd cannot search or download until it is logged in to the network
            if (application?.Server?.IsLoggedIn != true)
            {
                return new NzbDroneValidationFailure(string.Empty, $"slskd is not logged in to the Soulseek network, it reports '{application?.Server?.State}'")
                {
                    InfoLink = HttpRequestBuilder.BuildBaseUrl(Settings.UseSsl, Settings.Host, Settings.Port, Settings.UrlBase),
                    DetailedDescription = "slskd answers and accepts the API key, but it is not connected to Soulseek. Check its Soulseek credentials and connection in slskd.",
                };
            }

            // Settings slskd accepts but that break the integration are fixed in slskd's own
            // configuration. Test reports them as a warning, which still lets the client be saved.
            var issues = SlskdConfigIssues.Find(config);
            if (issues.Any())
            {
                return new NzbDroneValidationFailure(string.Empty, $"{string.Join(". ", issues)}. Change this in slskd's configuration")
                {
                    IsWarning = true
                };
            }

            return null;
        }

        /// <summary>
        /// Refuses an instance older than the one the plugin is built against. Everything that ties a
        /// transfer back to the grab that started it rests on the download id living in the batch
        /// destination, which older versions cannot be told to use.
        /// </summary>
        private ValidationFailure TestVersion(Application application)
        {
            if (SlskdCapabilities.IsSupported(application?.Version))
            {
                return null;
            }

            return new NzbDroneValidationFailure(string.Empty, $"Slskd {SlskdCapabilities.MinimumVersion} or newer is required")
            {
                InfoLink = HttpRequestBuilder.BuildBaseUrl(Settings.UseSsl, Settings.Host, Settings.Port, Settings.UrlBase),
                DetailedDescription = $"This slskd instance is older than {SlskdCapabilities.MinimumVersion}. Lidarr cannot track downloads on it, " +
                                      "because the completed download location cannot be pinned and a transfer can no longer be matched to what was grabbed. " +
                                      "Upgrade slskd and test again.",
            };
        }
    }
}
