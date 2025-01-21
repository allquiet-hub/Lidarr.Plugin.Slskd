namespace NzbDrone.Plugin.Slskd.Models;

public enum TransferStates
{
    None,
    Requested,
    Queued,
    Initializing,
    InProgress,
    Completed
}

public enum TransferSubStates
{
    // Only completed
    Succeeded,
    Cancelled,
    TimedOut,
    Errored,
    Rejected,
    Aborted,

    // Only queued
    Locally,
    Remotely,

    // Fake substate for the others
    NoSubState
}
