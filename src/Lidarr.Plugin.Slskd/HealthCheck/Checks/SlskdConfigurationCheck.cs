using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Slskd;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    /// <summary>
    /// Warns about slskd settings that break or degrade the integration in ways slskd itself never
    /// reports, because on its side they are legitimate configuration.
    ///
    /// Only settings with a concrete failure mode are checked. A destination permission mode with no
    /// execute bit is chmod'ed onto the album directories as well as the files, leaving them
    /// untraversable by anyone but root, and imports fail with a permission error that nothing ties
    /// back to the slskd config. Remote file management being disabled blocks the file delete API the
    /// plugin uses to clean up after an import, so completed downloads accumulate forever.
    /// </summary>
    [CheckOn(typeof(ProviderAddedEvent<IDownloadClient>))]
    [CheckOn(typeof(ProviderUpdatedEvent<IDownloadClient>))]
    [CheckOn(typeof(ProviderDeletedEvent<IDownloadClient>))]
    public class SlskdConfigurationCheck : HealthCheckBase
    {
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly ISlskdProxy _proxy;

        public SlskdConfigurationCheck(IProvideDownloadClient downloadClientProvider,
            ISlskdProxy proxy,
            ILocalizationService localizationService)
            : base(localizationService)
        {
            _downloadClientProvider = downloadClientProvider;
            _proxy = proxy;
        }

        public override HealthCheck Check()
        {
            var issues = new List<string>();

            foreach (var client in _downloadClientProvider.GetDownloadClients().OfType<Download.Clients.Slskd.Slskd>())
            {
                if (client.Definition?.Settings is not SlskdSettings settings)
                {
                    continue;
                }

                Plugin.Slskd.Models.SlskdOptions options;

                try
                {
                    options = _proxy.GetOptions(settings);
                }
                catch (Exception)
                {
                    // Unreachable instances are already surfaced by the client's own test and status
                    continue;
                }

                issues.AddRange(Plugin.Slskd.Helpers.SlskdConfigIssues.Find(options).Select(i => i.Description));
            }

            if (issues.Empty())
            {
                return new HealthCheck(GetType());
            }

            return new HealthCheck(GetType(), HealthCheckResult.Warning, string.Join(". ", issues), "#slskd-configuration");
        }
    }
}
