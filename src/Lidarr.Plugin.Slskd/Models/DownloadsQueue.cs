using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class DownloadsQueue
{
    [JsonProperty("username")]
    public string Username { get; set; }

    [JsonProperty("directories")]
    public List<DownloadDirectory> Directories { get; set; }
}
