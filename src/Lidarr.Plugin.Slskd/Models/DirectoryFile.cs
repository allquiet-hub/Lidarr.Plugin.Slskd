using System;
using Newtonsoft.Json;
using NzbDrone.Plugin.Slskd.Interfaces;

namespace NzbDrone.Plugin.Slskd.Models;

public class DirectoryFile : SlskdFile
{
    [JsonProperty("averageSpeed")]
    public double AverageSpeed { get; set; }

    [JsonProperty("bytesRemaining")]
    public long BytesRemaining { get; set; }

    [JsonProperty("bytesTransferred")]
    public long BytesTransferred { get; set; }

    [JsonProperty("direction")]
    public TransferDirection Direction { get; set; }

    [JsonProperty("endedAt")]
    public DateTime? EndedAt { get; set; }

    [JsonProperty("enqueuedAt")]
    public DateTime EnqueuedAt { get; set; }

    [JsonProperty("elapsedTime")]
    public TimeSpan ElapsedTime { get; set; }

    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("percentComplete")]
    public double PercentComplete { get; set; }

    [JsonProperty("remainingTime")]
    public TimeSpan RemainingTime { get; set; }

    [JsonProperty("requestedAt")]
    public DateTime RequestedAt { get; set; }

    [JsonProperty("startOffset")]
    public int StartOffset { get; set; }

    [JsonProperty("startedAt")]
    public DateTime? StartedAt { get; set; }

    [JsonIgnore]
    private string _state;

    /// <summary>
    /// TransferStates
    /// </summary>
    [JsonProperty("state")]
    public string State
    {
        get => _state;
        set
        {
            _state = value;

            if (value == null)
            {
                return;
            }

            var parts = value.Split(',', StringSplitOptions.TrimEntries);
            TransferState = new TransferStates
            {
                State = Enum.Parse<TransferStateEnum>(parts[0], true),
                Substate = parts.Length > 1 ? Enum.Parse<TransferStateEnum>(parts[1], true) : TransferStateEnum.None
            };
        }
    }

    [JsonIgnore]
    public TransferStates TransferState { get; set; }

    /// <summary>
    /// Gets the username of the peer to or from which the file is to be transferred.
    /// </summary>
    [JsonProperty("username")]
    public string Username { get; set; }

    /// <summary>
    /// Gets the Exception that caused the failure of the transfer, if applicable.
    /// </summary>
    [JsonProperty("exception")]
    public string Exception { get; set; }
}
