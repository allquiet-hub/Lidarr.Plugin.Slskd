using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class UserDirectory
{
    [JsonProperty("name")]
    public string DirectoryPath { get; set; }

    [JsonProperty("fileCount")]
    public int FileCount { get; set; }

    [JsonProperty("files")]
    public List<SearchResponseFile> Files { get; set; }
}
