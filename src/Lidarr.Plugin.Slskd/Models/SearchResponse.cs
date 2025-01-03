using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class SearchResponse
{
    [JsonProperty("username")]
    public string Username { get; set; }

    [JsonProperty("hasFreeUploadSlot")]
    public bool HasFreeUploadSlot { get; set; }

    [JsonProperty("token")]
    public int Token { get; set; }

    [JsonProperty("queueLength")]
    public int QueueLength { get; set; }

    [JsonProperty("uploadSpeed")]
    public int UploadSpeed { get; set; }

    [JsonProperty("fileCount")]
    public int FileCount { get; set; }

    [JsonProperty("files")]
    public List<SearchResponseFile> Files { get; set; }

    [JsonProperty("lockedFileCount")]
    public int LockedFileCount { get; set; }

    [JsonProperty("lockedFiles")]
    public List<SearchResponseFile> LockedFiles { get; set; }
}
