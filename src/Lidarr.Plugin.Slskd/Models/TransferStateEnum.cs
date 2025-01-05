namespace NzbDrone.Plugin.Slskd.Models;

public enum TransferStateEnum
{
    None,
    Requested,
    Queued,
    Initializing,
    InProgress,
    Completed,
    Succeeded,
    Cancelled,
    TimedOut,
    Errored,
    Rejected,
    Aborted,
    Locally,
    Remotely
}
