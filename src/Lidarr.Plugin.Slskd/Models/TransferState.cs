namespace NzbDrone.Plugin.Slskd.Models;

public class TransferState
{
    public TransferStates State { get; set; }
    public TransferSubStates SubState { get; set; }
}
