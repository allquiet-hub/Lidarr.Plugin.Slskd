using NLog;
using NzbDrone.Core.Indexers.Slskd;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    /// <summary>
    /// Rejects a result of the album-title-only query whose path never names the artist.
    ///
    /// That query exists for records whose artist the Soulseek server refuses to answer for, and their
    /// folders still carry the artist's name. What it also returns is every other artist's record or
    /// song sharing the title, and those reach the album anyway: the title is given the library's artist
    /// so that it maps, which is right for a query that named the artist and says nothing here. Measured
    /// on an artist search, the title of a single matched five KRS-One tracks that were approved for it.
    ///
    /// The verdict is a rejection rather than a filter: the result stays visible in interactive search,
    /// mapped to the album it was found for, and can still be grabbed by hand when the sharer simply left
    /// the artist out of the path.
    /// </summary>
    public class SlskdArtistInPathSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public SlskdArtistInPathSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteAlbum subject, SearchCriteriaBase searchCriteria)
        {
            if (subject.Release is not SlskdReleaseInfo { ArtistMissingFromPath: true })
            {
                return Decision.Accept();
            }

            var message = $"Found by searching the album title alone, and nothing in its folder or file names mentions " +
                          $"{subject.Artist?.Name ?? "the artist"}: most likely another artist's record with the same title";

            _logger.Debug(message);
            return Decision.Reject(message);
        }
    }
}
