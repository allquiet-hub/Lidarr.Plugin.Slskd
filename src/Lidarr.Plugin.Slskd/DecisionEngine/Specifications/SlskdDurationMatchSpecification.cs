using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Indexers.Slskd;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    /// <summary>
    /// Rejects Soulseek results whose file durations fit no edition of the album being searched.
    ///
    /// A folder can carry the album's name and a plausible file count while holding entirely
    /// different recordings — a radio show, a live set, a bootleg of re-edits — and nothing in
    /// Lidarr's decision engine can see that: it judges a title, never the audio behind it. Such a
    /// folder downloads in full and is then refused by the import on the real tracks' evidence.
    /// Durations close that gap early, because Soulseek peers report a playback length per file, and
    /// the wrong recording of a track almost never has the right length: measured against a real
    /// mismatch, the album's own copies mapped 17 of 17 tracks within 5 seconds while radio-show
    /// folders sharing its name mapped 8 to 11, drifting by 6-13 seconds on average.
    ///
    /// The verdict is a rejection rather than a filter: the result stays visible in interactive
    /// search with this reason beside it and can still be grabbed by hand.
    /// </summary>
    public class SlskdDurationMatchSpecification : IDecisionEngineSpecification
    {
        /// <summary>
        /// How far a file's duration may sit from a track's before they stop counting as the same
        /// recording. Peers report whole seconds and different encodes of one recording differ by a
        /// second or two of padding; different recordings differ by far more.
        /// </summary>
        private const int ToleranceSeconds = 5;

        /// <summary>
        /// Fraction of the comparable tracks that must map onto files for the folder to pass. Real
        /// copies of an album map completely, so anything materially below 1.0 works; 0.8 leaves
        /// room for a single odd file on a long album without letting the measured mismatches
        /// through, which reached 0.65 at their best.
        /// </summary>
        private const double RequiredMatchFraction = 0.8;

        /// <summary>
        /// Fraction of either side that has to carry usable durations before the check may judge.
        /// A peer that reports no lengths, or a MusicBrainz edition without them (vinyl entries
        /// often lack track lengths), leaves nothing to compare — the folder is given the benefit
        /// of the doubt rather than punished for missing data.
        /// </summary>
        private const double MinimumDurationCoverage = 0.8;

        private readonly Logger _logger;

        public SlskdDurationMatchSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteAlbum subject, SearchCriteriaBase searchCriteria)
        {
            if (subject.Release is not SlskdReleaseInfo release || release.FileDurations == null)
            {
                return Decision.Accept();
            }

            var fileDurations = release.FileDurations.Where(d => d > 0).OrderBy(d => d).ToList();
            if (release.FileDurations.Count == 0 ||
                fileDurations.Count < release.FileDurations.Count * MinimumDurationCoverage)
            {
                return Decision.Accept();
            }

            var album = subject.Albums?.FirstOrDefault();
            var albumReleases = album?.AlbumReleases?.Value;
            if (albumReleases == null || albumReleases.Count == 0)
            {
                return Decision.Accept();
            }

            var eligible = albumReleases.Where(r => r.Monitored || album.AnyReleaseOk).ToList();
            if (eligible.Count == 0)
            {
                eligible = albumReleases;
            }

            var judged = false;
            var bestMatched = 0;
            var bestComparable = 0;
            double bestFraction = -1;

            foreach (var albumRelease in eligible)
            {
                var tracks = albumRelease.Tracks?.Value;
                if (tracks == null || tracks.Count == 0)
                {
                    continue;
                }

                // Track durations are stored in milliseconds; peers report whole seconds
                var trackDurations = tracks
                    .Select(t => (int)Math.Round(t.Duration / 1000.0))
                    .Where(d => d > 0)
                    .OrderBy(d => d)
                    .ToList();

                if (trackDurations.Count < tracks.Count * MinimumDurationCoverage)
                {
                    continue;
                }

                judged = true;

                // The folder is judged on what it holds, not on how much: the fraction runs over the
                // overlap, so a partial folder whose files all belong to the album passes here and is
                // left to the completeness check, keeping this verdict orthogonal to that one.
                var comparable = Math.Min(trackDurations.Count, fileDurations.Count);
                var matched = CountMatches(trackDurations, fileDurations);
                var fraction = comparable > 0 ? (double)matched / comparable : 0;

                if (fraction > bestFraction)
                {
                    bestFraction = fraction;
                    bestMatched = matched;
                    bestComparable = comparable;
                }

                if (fraction >= RequiredMatchFraction)
                {
                    return Decision.Accept();
                }
            }

            // Every edition either lacked MusicBrainz durations or had none to compare against
            if (!judged)
            {
                return Decision.Accept();
            }

            var message = $"Track durations fit no edition of the album: {bestMatched} of {bestComparable} files match " +
                          $"within {ToleranceSeconds}s against the closest release. The folder likely holds different " +
                          $"recordings, which Lidarr's import would refuse after the download completes";

            _logger.Debug(message);
            return Decision.Reject(message);
        }

        /// <summary>
        /// Largest one-to-one pairing between two ascending duration lists with each pair within the
        /// tolerance. One-to-one on purpose: nearest-match would let one file of a common length
        /// satisfy several tracks, and a folder of ten identical jingles read as an album.
        /// </summary>
        private static int CountMatches(IReadOnlyList<int> trackSeconds, IReadOnlyList<int> fileSeconds)
        {
            var matches = 0;
            var trackIndex = 0;
            var fileIndex = 0;

            while (trackIndex < trackSeconds.Count && fileIndex < fileSeconds.Count)
            {
                var difference = fileSeconds[fileIndex] - trackSeconds[trackIndex];

                if (Math.Abs(difference) <= ToleranceSeconds)
                {
                    matches++;
                    trackIndex++;
                    fileIndex++;
                }
                else if (difference < 0)
                {
                    fileIndex++;
                }
                else
                {
                    trackIndex++;
                }
            }

            return matches;
        }
    }
}
