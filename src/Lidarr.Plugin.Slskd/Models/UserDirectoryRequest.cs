using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class UserDirectoryRequest
{
    [JsonProperty("directory")]
    public string DirectoryPath { get; set; }
}
