using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class DownloadDirectory
{
    [JsonProperty("directory")]
    public string Directory { get; set; }

    [JsonProperty("fileCount")]
    public int FileCount { get; set; }

    [JsonProperty("files")]
    public List<DirectoryFile> Files { get; set; }
}
