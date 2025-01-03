using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class ApplicationServer
{
    [JsonProperty("address")]
    public string Address { get; set; }

    [JsonProperty("ipEndPoint")]
    public string IpEndPoint { get; set; }

    [JsonProperty("state")]
    public string State { get; set; }

    [JsonProperty("isConnected")]
    public bool IsConnected { get; set; }

    [JsonProperty("isLoggedIn")]
    public bool IsLoggedIn { get; set; }

    [JsonProperty("isTransitioning")]
    public bool IsTransitioning { get; set; }
}
