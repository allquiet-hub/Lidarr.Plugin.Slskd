using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class UserStatus
{
    [JsonProperty("isPrivileged")]
    public string IsPrivileged { get; set; }

    [JsonProperty("presence")]
    public string Presence { get; set; }

    [JsonIgnore]
    public bool IsOnline => Presence == "Online";
}
