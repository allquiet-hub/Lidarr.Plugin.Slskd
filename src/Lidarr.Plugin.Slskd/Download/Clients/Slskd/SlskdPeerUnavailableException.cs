using System;

namespace NzbDrone.Core.Download.Clients.Slskd
{
    /// <summary>
    /// Raised when slskd accepted the request but could not reach the peer holding the files.
    ///
    /// This says nothing about the health of slskd or of the indexer, only that this particular release
    /// cannot be fetched right now, so the download client translates it into a ReleaseUnavailableException
    /// and Lidarr moves on to the next candidate.
    /// </summary>
    public class SlskdPeerUnavailableException : Exception
    {
        public SlskdPeerUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
