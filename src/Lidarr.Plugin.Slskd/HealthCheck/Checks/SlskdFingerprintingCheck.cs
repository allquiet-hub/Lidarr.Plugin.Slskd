using System.Linq;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Slskd;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    /// <summary>
    /// Warns when audio fingerprinting is enabled while a Slskd download client is in use.
    ///
    /// Files shared on Soulseek frequently carry MusicBrainz tags whose recording IDs disagree with the
    /// ones Lidarr holds for the release. That mismatch alone is weighted 10 in the track distance
    /// (Distance.cs) and fingerprinting adds a second, independent penalty for the same key, roughly
    /// halving the resulting match score. Since CloseAlbumMatchSpecification rejects an album on its
    /// worst track, a single penalised track is enough to fail the whole import.
    /// </summary>
    [CheckOn(typeof(ConfigSavedEvent))]
    [CheckOn(typeof(ProviderAddedEvent<IDownloadClient>))]
    [CheckOn(typeof(ProviderUpdatedEvent<IDownloadClient>))]
    [CheckOn(typeof(ProviderDeletedEvent<IDownloadClient>))]
    public class SlskdFingerprintingCheck : HealthCheckBase
    {
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IConfigService _configService;

        public SlskdFingerprintingCheck(IProvideDownloadClient downloadClientProvider,
            IConfigService configService,
            ILocalizationService localizationService)
            : base(localizationService)
        {
            _downloadClientProvider = downloadClientProvider;
            _configService = configService;
        }

        public override HealthCheck Check()
        {
            if (_configService.AllowFingerprinting == AllowFingerprinting.Never)
            {
                return new HealthCheck(GetType());
            }

            if (!_downloadClientProvider.GetDownloadClients().Any(client => client is Slskd))
            {
                return new HealthCheck(GetType());
            }

            var message = "Audio fingerprinting is enabled, which makes Slskd downloads more likely to fail automatic import. " +
                          "Set Media Management > Allow Fingerprinting to 'Never': fingerprinting adds a recording ID penalty on " +
                          "top of any mismatch already present in the file tags, which pushes the track match below the import threshold.";

            return new HealthCheck(GetType(), HealthCheckResult.Warning, message, "#allow-fingerprinting-is-enabled");
        }
    }
}
