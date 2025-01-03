using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class SlskdOptions
{
    [JsonProperty("directories")]
    public SlskdOptionsDirectories Directories { get; set; }
}

public class SlskdOptionsDirectories
{
    [JsonProperty("downloads")]
    public string Downloads { get; set; }

    [JsonProperty("incomplete")]
    public string Incomplete { get; set; }
}
