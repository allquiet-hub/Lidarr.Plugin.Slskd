using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Validation;

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
            return Task.FromResult(_proxy.Download(release.Origin, release.Source, release.DownloadUrl, Settings));
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

            // if (!config.CreateAlbumFolder)
            // {
            //     return new NzbDroneValidationFailure(string.Empty, "Slskd must have 'Create Album Folders' enabled")
            //     {
            //         InfoLink = HttpRequestBuilder.BuildBaseUrl(Settings.UseSsl, Settings.Host, Settings.Port, Settings.UrlBase),
            //         DetailedDescription = "Slskd must have 'Create Album Folders' enabled, otherwise Lidarr will not be able to import the downloads",
            //     };
            // }
            //
            // if (!config.CreateSingleFolder)
            // {
            //     return new NzbDroneValidationFailure(string.Empty, "Slskd must have 'Create folder structure for singles' enabled")
            //     {
            //         InfoLink = HttpRequestBuilder.BuildBaseUrl(Settings.UseSsl, Settings.Host, Settings.Port, Settings.UrlBase),
            //         DetailedDescription = "Slskd must have 'Create folder structure for singles' enabled, otherwise Lidarr will not be able to import single downloads",
            //     };
            // }
            var connectivity = _proxy.TestConnectivity(Settings);
            if (!connectivity)
            {
                return new NzbDroneValidationFailure(string.Empty, "Could not connect to Slskd")
                {
                    InfoLink = HttpRequestBuilder.BuildBaseUrl(Settings.UseSsl, Settings.Host, Settings.Port, Settings.UrlBase),
                    DetailedDescription = "Could not connect to Slskd, please check your settings",
                };
            }

            return null;
        }
    }
}
