using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class Application
{
    [JsonProperty("version")]
    public ApplicationVersion Version { get; set; }

    [JsonProperty("pendingReconnect")]
    public bool PendingReconnect { get; set; }

    [JsonProperty("pendingRestart")]
    public bool PendingRestart { get; set; }

    [JsonProperty("server")]
    public ApplicationServer Server { get; set; }
}
