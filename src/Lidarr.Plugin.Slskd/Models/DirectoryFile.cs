using System;
using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class DirectoryFile : SlskdFile
{
    [JsonProperty("averageSpeed")]
    public double AverageSpeed { get; set; }

    /// <summary>
    /// Gets the identifier of the batch this transfer belongs to, if it was enqueued as part of one.
    /// Available from slskd 0.26.0 onwards; null for transfers enqueued through the legacy endpoint.
    /// </summary>
    [JsonProperty("batchId")]
    public string BatchId { get; set; }

    /// <summary>
    /// Gets the number of times the transfer has been attempted (slskd 0.26.0+).
    /// </summary>
    [JsonProperty("attempts")]
    public int Attempts { get; set; }

    /// <summary>
    /// Gets the time at which slskd will retry the transfer, if a retry is scheduled (slskd 0.26.0+).
    /// </summary>
    [JsonProperty("nextAttemptAt")]
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>
    /// Gets a value indicating whether the transfer has been removed from the queue (slskd 0.26.0+).
    /// </summary>
    [JsonProperty("removed")]
    public bool Removed { get; set; }

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
            TransferState = new TransferState
            {
                State = Enum.Parse<TransferStates>(parts[0], true),
                SubState = parts.Length > 1 ? Enum.Parse<TransferSubStates>(parts[1], true) : TransferSubStates.NoSubState
            };
        }
    }

    [JsonIgnore]
    public TransferState TransferState { get; set; }

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
