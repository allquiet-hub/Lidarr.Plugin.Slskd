using NLog;
using NzbDrone.Core.Indexers.Slskd;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    /// <summary>
    /// Rejects Soulseek results whose audio file count cannot fit the album: fewer files than the
    /// album has tracks, or more files than its largest eligible release can absorb.
    ///
    /// Both directions end the same way without this check — the release is grabbed, transferred in
    /// full, and then refused by the import, with 'Has missing tracks' for a folder the peer only
    /// partially shares and 'Has unmatched tracks' for a folder holding extra songs. The excess case
    /// is common for small releases: Soulseek matches search terms against the whole path, so a folder
    /// whose name carries the terms returns every file it contains, unrelated songs included.
    /// Rejecting here keeps the result visible in interactive search together with the reason, so it
    /// can still be forced by hand.
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

            if (release.MaximumTrackCount > 0 && release.AudioFileCount > release.MaximumTrackCount)
            {
                var excessMessage = $"Oversized release: {release.AudioFileCount} audio files for an album whose largest " +
                                    $"release has {release.MaximumTrackCount} tracks. Lidarr would fail to import it with 'Has unmatched tracks'";

                _logger.Debug(excessMessage);
                return Decision.Reject(excessMessage);
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
