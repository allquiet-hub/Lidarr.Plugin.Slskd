using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

/// <summary>
/// Body of POST /api/v0/transfers/downloads/batches (slskd 0.26.0+).
/// </summary>
public class EnqueueBatchRequest
{
    [JsonProperty("searchId", NullValueHandling = NullValueHandling.Ignore)]
    public string SearchId { get; set; }

    [JsonProperty("username")]
    public string Username { get; set; }

    [JsonProperty("files")]
    public List<DownloadRequest> Files { get; set; }

    [JsonProperty("options")]
    public BatchOptions Options { get; set; }
}

public class EnqueueBatchResponse
{
    [JsonProperty("batch")]
    public Batch Batch { get; set; }

    [JsonProperty("failures")]
    public List<EnqueueBatchFailure> Failures { get; set; } = new ();
}

public class EnqueueBatchFailure
{
    [JsonProperty("filename")]
    public string Filename { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; }
}
