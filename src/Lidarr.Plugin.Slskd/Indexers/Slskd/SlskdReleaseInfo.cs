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
    /// A grab from interactive search still carries these counts, since it resolves back to the cached
    /// RemoteAlbum rather than to the resource the client posted, but it goes straight to the download
    /// without the decision engine running again. The rejection therefore stops the automatic path and
    /// leaves the same release grabbable by hand, which is the intent.
    /// </summary>
    public class SlskdReleaseInfo : ReleaseInfo
    {
        /// <summary>
        /// Audio files in the folder, disc sub-folders included.
        /// </summary>
        public int AudioFileCount { get; set; }

        /// <summary>
        /// Tracks the album is expected to have, or 0 when unknown or when the user allows incomplete releases.
        /// </summary>
        public int ExpectedTrackCount { get; set; }

        /// <summary>
        /// The largest track count among the releases the import may map against, or 0 when unknown.
        /// </summary>
        public int MaximumTrackCount { get; set; }
    }
}
