using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

/// <summary>
/// A group of transfers enqueued together, available from slskd 0.26.0 onwards.
/// </summary>
public class Batch
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("searchId")]
    public string SearchId { get; set; }

    [JsonProperty("username")]
    public string Username { get; set; }

    [JsonProperty("direction")]
    public TransferDirection Direction { get; set; }

    [JsonProperty("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("options")]
    public BatchOptions Options { get; set; }

    [JsonProperty("transfers")]
    public List<DirectoryFile> Transfers { get; set; }
}

public class BatchOptions
{
    /// <summary>
    /// Destination directory for the files in the batch, relative to the configured downloads directory.
    /// Takes precedence over the 'transfers.download.destination.subdirectory' expression configured in slskd.
    /// </summary>
    [JsonProperty("destination")]
    public string Destination { get; set; }

    /// <summary>
    /// Accepted on enqueue but never returned by slskd 0.26.0, so it cannot be used to correlate a batch
    /// back to a Lidarr download. It is still sent so the '${BATCH_EXTERNAL_ID}' token resolves for users
    /// who reference it in their own destination expression.
    /// </summary>
    [JsonProperty("externalId")]
    public string ExternalId { get; set; }
}
