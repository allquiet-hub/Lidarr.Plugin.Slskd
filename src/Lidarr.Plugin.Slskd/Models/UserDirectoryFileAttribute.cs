using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class UserDirectoryFileAttribute
{
    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("value")]
    public int Value { get; set; }
}
