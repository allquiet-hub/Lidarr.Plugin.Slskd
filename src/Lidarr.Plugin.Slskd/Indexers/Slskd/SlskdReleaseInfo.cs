using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.Slskd
{
    /// <summary>
    /// Release enriched with the file counts needed to judge whether it can actually be imported.
    ///
    /// Lidarr's decision engine has no way to see how many audio files a Soulseek result holds, so an
    /// incomplete folder is grabbed happily and only fails at import time with 'Has missing tracks',
    /// once the whole download has been transferred. SlskdCompleteReleaseSpecification uses these counts
    /// to reject it up front instead.
    ///
    /// ReleaseResource.ToModel rebuilds a plain ReleaseInfo for non-torrent protocols, so this data is
    /// lost when a release is grabbed by hand from interactive search. That is intentional: automatic
    /// grabs are blocked while manual ones remain possible.
    /// </summary>
    public class SlskdReleaseInfo : ReleaseInfo
    {
        /// <summary>
        /// Number of valid audio files in the release folder, disc sub-folders included.
        /// </summary>
        public int AudioFileCount { get; set; }

        /// <summary>
        /// Tracks the album is expected to have, or 0 when unknown or when the user allows incomplete releases.
        /// </summary>
        public int ExpectedTrackCount { get; set; }
    }
}
