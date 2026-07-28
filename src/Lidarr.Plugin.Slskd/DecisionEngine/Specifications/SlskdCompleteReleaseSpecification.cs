using NLog;
using NzbDrone.Core.Indexers.Slskd;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    /// <summary>
    /// Rejects Soulseek results that hold fewer audio files than the album has tracks.
    ///
    /// Without this the release is grabbed, transferred in full, and then refused by the import with
    /// 'Has missing tracks' — the peer simply does not share the whole album. Rejecting here keeps the
    /// result visible in interactive search together with the reason, so it can still be forced by hand.
    /// </summary>
    public class SlskdCompleteReleaseSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public SlskdCompleteReleaseSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteAlbum subject, SearchCriteriaBase searchCriteria)
        {
            if (subject.Release is not SlskdReleaseInfo release)
            {
                return Decision.Accept();
            }

            // Unknown track count, or the user opted into incomplete releases
            if (release.ExpectedTrackCount <= 0)
            {
                return Decision.Accept();
            }

            if (release.AudioFileCount >= release.ExpectedTrackCount)
            {
                return Decision.Accept();
            }

            var message = $"Incomplete release: {release.AudioFileCount} audio files for an album with " +
                          $"{release.ExpectedTrackCount} tracks. Lidarr would fail to import it with 'Has missing tracks'";

            _logger.Debug(message);
            return Decision.Reject(message);
        }
    }
}
