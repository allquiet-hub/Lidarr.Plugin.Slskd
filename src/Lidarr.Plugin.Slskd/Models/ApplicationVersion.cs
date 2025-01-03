using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class ApplicationVersion
{
    [JsonProperty("full")]
    public string Full { get; set; }

    [JsonProperty("current")]
    public string Current { get; set; }

    [JsonProperty("latest")]
    public string Latest { get; set; }

    [JsonProperty("isUpdateAvailable")]
    public bool IsUpdateAvailable { get; set; }

    [JsonProperty("isCanary")]
    public bool IsCanary { get; set; }

    [JsonProperty("isDevelopment")]
    public bool IsDevelopment { get; set; }
}
