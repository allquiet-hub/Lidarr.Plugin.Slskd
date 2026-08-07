using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
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
                // The identifier travels with the release instead of being recomputed from the download
                // path: that path is the folder for albums but the file itself for single-file releases,
                // so deriving it a second time here would not always agree with what the queue reports.
                // The completed folder is named "Artist - Album" because that name does double duty:
                // Lidarr identifies what it imported largely from it, and the queue maps a download back
                // to its album only by parsing a title, never by the ids already sitting in history.
                // A parsed title resolves through normalised lookups that forgive punctuation, while the
                // history fallback demands the library's exact punctuation inside a stranger's folder name.
                var album = remoteAlbum.Albums?.FirstOrDefault();
                var artistName = remoteAlbum.Artist?.Name ?? album?.Artist?.Value?.Name;
                var folderName = album?.Title == null || artistName == null
                    ? album?.Title
                    : $"{artistName} - {album.Title}";

                return Task.FromResult(_proxy.Download(release.Origin, release.Source, release.DownloadUrl, release.Guid, folderName, Settings));
            }
            catch (SlskdPeerUnavailableException ex)
            {
                // Reported as unavailable rather than as a download failure, so Lidarr tries the next
                // release without holding the indexer responsible for an unreachable peer
                throw new ReleaseUnavailableException(release, ex.Message, ex);
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

            var connectivity = _proxy.TestConnectivity(Settings);
            if (!connectivity)
            {
                return new NzbDroneValidationFailure(string.Empty, "Could not connect to Slskd")
                {
                    InfoLink = HttpRequestBuilder.BuildBaseUrl(Settings.UseSsl, Settings.Host, Settings.Port, Settings.UrlBase),
                    DetailedDescription = "Could not connect to Slskd, please check your settings",
                };
            }

            if (Settings.RepairConfiguration)
            {
                var repairResult = RepairConfiguration(config);
                if (repairResult != null)
                {
                    return repairResult;
                }
            }

            return TestBatchSupport();
        }

        /// <summary>
        /// Rewrites the slskd settings that break the integration, restarting slskd when the change
        /// needs one — but never while transfers are active, because a restart kills them. Every
        /// outcome is reported through a validation warning so the user learns what happened, or what
        /// still has to be done by hand, directly from the Test button.
        /// </summary>
        private ValidationFailure RepairConfiguration(SlskdOptions options)
        {
            var issues = SlskdConfigIssues.Find(options);
            if (issues.Count == 0)
            {
                return null;
            }

            var issueNames = string.Join(", ", issues.Select(i => i.ShortName));

            try
            {
                string yaml;

                try
                {
                    yaml = _proxy.GetOptionsYaml(Settings);
                }
                catch (HttpException httpException) when (httpException.Response?.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return RepairWarning($"slskd configuration needs fixing ({issueNames}), but editing it remotely requires an API key " +
                                         "with the administrator role and 'remote_configuration: true' in slskd. Fix it manually instead.");
                }

                var changed = false;
                var unfixable = new List<string>();

                foreach (var issue in issues)
                {
                    var (edited, editChanged, found) = SlskdYamlEditor.SetValue(yaml, issue.YamlPath, issue.DesiredValue);
                    if (!found)
                    {
                        unfixable.Add(issue.ShortName);
                        continue;
                    }

                    yaml = edited;
                    changed |= editChanged;
                }

                if (changed)
                {
                    var validationError = _proxy.ValidateOptionsYaml(yaml, Settings);
                    if (validationError != null)
                    {
                        return RepairWarning($"The rewritten slskd configuration failed slskd's own validation and was not saved: {validationError}. " +
                                             $"Fix manually: {issueNames}.");
                    }

                    _proxy.SaveOptionsYaml(yaml, Settings);
                    _logger.Info($"Rewrote slskd configuration ({issueNames})");
                }

                if (unfixable.Any())
                {
                    return RepairWarning($"Could not locate {string.Join(", ", unfixable)} in slskd's configuration file, fix manually.");
                }

                if (_proxy.GetApplication(Settings)?.PendingRestart != true)
                {
                    // slskd watches its configuration file and applies what it can without a restart
                    return SlskdConfigIssues.Find(_proxy.GetOptions(Settings)).Count == 0
                        ? RepairWarning($"slskd configuration was repaired ({issueNames}), no restart was needed.")
                        : RepairWarning("slskd's config file was updated but the running instance still reports the old values, restart slskd manually.");
                }

                var activeDownloads = _proxy.CountActiveDownloads(Settings);
                if (activeDownloads > 0)
                {
                    return RepairWarning($"slskd's config file was updated but a restart is needed to apply it, and {activeDownloads} downloads are active. " +
                                         "Restart slskd manually or run Test again when the queue is idle.");
                }

                try
                {
                    _proxy.Restart(Settings);
                }
                catch (HttpException httpException) when (httpException.Response?.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return RepairWarning("slskd's config file was updated but restarting slskd requires an API key with the administrator role, restart it manually.");
                }

                if (!WaitForRestart())
                {
                    return RepairWarning("slskd is restarting to apply the repaired configuration, run Test again in a moment.");
                }

                return SlskdConfigIssues.Find(_proxy.GetOptions(Settings)).Count == 0
                    ? RepairWarning($"slskd configuration was repaired ({issueNames}) and slskd was restarted.")
                    : RepairWarning($"slskd was restarted but still reports problems ({issueNames}), fix manually.");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Repairing the slskd configuration failed");
                return RepairWarning($"Repairing the slskd configuration failed ({ex.Message}), fix manually: {issueNames}.");
            }
        }

        private bool WaitForRestart()
        {
            var deadline = DateTime.UtcNow.AddSeconds(45);

            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));

                try
                {
                    if (_proxy.GetApplication(Settings) != null)
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // Still coming back up
                }
            }

            return false;
        }

        private static NzbDroneValidationFailure RepairWarning(string message)
        {
            return new NzbDroneValidationFailure(string.Empty, message) { IsWarning = true };
        }

        private ValidationFailure TestBatchSupport()
        {
            if (!_proxy.SupportsBatches(Settings))
            {
                return new NzbDroneValidationFailure(string.Empty, $"Slskd {SlskdCapabilities.BatchesMinimumVersion} or newer is recommended")
                {
                    IsWarning = true,
                    InfoLink = HttpRequestBuilder.BuildBaseUrl(Settings.UseSsl, Settings.Host, Settings.Port, Settings.UrlBase),
                    DetailedDescription = $"This slskd instance is older than {SlskdCapabilities.BatchesMinimumVersion}, so Lidarr cannot pin the completed download location " +
                                          "and has to infer it from the remote folder name. Downloads will not be imported if 'transfers.download.destination.subdirectory' " +
                                          "is customised in slskd. Upgrading slskd also enables automatic retries of failed transfers.",
                };
            }

            return null;
        }
    }
}
