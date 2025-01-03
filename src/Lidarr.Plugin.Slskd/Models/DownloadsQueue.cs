using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class DownloadsQueue
{
    [JsonProperty("username")]
    public string Username { get; set; }

    [JsonProperty("directories")]
    public List<DownloadDirectory> Directories { get; set; }
}

public class DownloadDirectory
{
    [JsonProperty("directory")]
    public string Directory { get; set; }

    [JsonProperty("fileCount")]
    public int FileCount { get; set; }

    [JsonProperty("files")]
    public List<DirectoryFile> Files { get; set; }
}

public class DirectoryFile
{
    /// <summary>
    /// Gets the current average transfer speed.
    /// </summary>
    [JsonProperty("averageSpeed")]
    public double AverageSpeed { get; set; }

    /// <summary>
    /// Gets the number of remaining bytes to be transferred.
    /// </summary>
    [JsonProperty("bytesRemaining")]
    public long BytesRemaining { get; set; }

    /// <summary>
    /// Gets the total number of bytes transferred.
    /// </summary>
    [JsonProperty("bytesTransferred")]
    public long BytesTransferred { get; set; }

    /// <summary>
    /// TransferDirection
    /// </summary>
    [JsonProperty("direction")]
    public TransferDirection Direction { get; set; }

    /// <summary>
    /// Gets the UTC time at which the transfer transitioned into the Soulseek.TransferStates.Completed state.
    /// </summary>
    [JsonProperty("endedAt")]
    public DateTime? EndedAt { get; set; }

    [JsonProperty("enqueuedAt")]
    public DateTime EnqueuedAt { get; set; }

    /// <summary>
    /// Gets the current duration of the transfer, if it has been started.
    /// </summary>
    [JsonProperty("elapsedTime")]
    public TimeSpan ElapsedTime { get; set; }

    [JsonIgnore]
    private string _fileName;

    /// <summary>
    /// Gets the filename of the file to be transferred.
    /// </summary>
    [JsonProperty("filename")]
    public string FileName
    {
        get => _fileName;
        set
        {
            _fileName = value;

            if (value == null)
            {
                return;
            }

            var parts = value.Split('\\');

            // File name itself
            Name = parts[^1];

            // Parent folder: Two levels above the file
            ParentFolder = parts.Length > 2
                ? string.Join("\\", parts[^3..^1]) // Two directories above the file
                : parts.Length > 1
                    ? parts[^2] // Single parent folder
                    : null;

            // Full path of the immediate parent directory
            ParentPath = parts.Length > 1
                ? string.Join("\\", parts[..^1]) // Full path excluding the file name
                : null;
        }
    }

    [JsonIgnore]
    public string Name { get; set; }

    [JsonIgnore]
    public string ParentFolder { get; set; }

    [JsonIgnore]
    public string ParentPath { get; set; }

    /// <summary>
    /// Gets the transfer id.
    /// </summary>
    [JsonProperty("id")]
    public string Id { get; set; }

    /// <summary>
    /// Gets the current progress in percent.
    /// </summary>
    [JsonProperty("percentComplete")]
    public double PercentComplete { get; set; }

    /// <summary>
    /// Gets the projected remaining duration of the transfer.
    /// </summary>
    [JsonProperty("remainingTime")]
    public TimeSpan RemainingTime { get; set; }

    [JsonProperty("requestedAt")]
    public DateTime RequestedAt { get; set; }

    /// <summary>
    /// Gets the size of the file to be transferred, in bytes.
    /// </summary>
    [JsonProperty("size")]
    public int Size { get; set; }

    /// <summary>
    /// Gets the starting offset of the transfer, in bytes.
    /// </summary>
    [JsonProperty("startOffset")]
    public int StartOffset { get; set; }

    /// <summary>
    /// Gets the UTC time at which the transfer transitioned into the Soulseek.TransferStates.InProgress state.
    /// </summary>
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

    [JsonIgnore]
    public string Extension { get; set; }
}

public enum TransferDirection
{
    Download,
    Upload
}

public class TransferStates
{
    public TransferStateEnum State { get; set; }
    public TransferStateEnum Substate { get; set; }
}

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
