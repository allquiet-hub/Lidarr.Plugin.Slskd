using System;

namespace NzbDrone.Core.Download.Clients.Slskd
{
    /// <summary>
    /// Raised when slskd would not enqueue some of the files of a release, most often because the same
    /// files are already being downloaded for another grab or outside Lidarr.
    ///
    /// Reported as a grab the download client rejected rather than one that went through, since slskd
    /// is not downloading those files under this grab and never will: Lidarr would otherwise track a
    /// download that either never appears in the queue or completes without them.
    /// </summary>
    public class SlskdEnqueueRefusedException : Exception
    {
        public SlskdEnqueueRefusedException(string message)
            : base(message)
        {
        }
    }
}
