using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class SlskdOptions
{
    [JsonProperty("directories")]
    public SlskdOptionsDirectories Directories { get; set; }

    [JsonProperty("soulseek")]
    public SlskdOptionsSoulseek Soulseek { get; set; }

    [JsonProperty("remoteFileManagement")]
    public bool RemoteFileManagement { get; set; }

    [JsonProperty("transfers")]
    public SlskdOptionsTransfers Transfers { get; set; }
}

public class SlskdOptionsTransfers
{
    [JsonProperty("download")]
    public SlskdOptionsDownload Download { get; set; }
}

public class SlskdOptionsDownload
{
    [JsonProperty("destination")]
    public SlskdOptionsDestination Destination { get; set; }
}

public class SlskdOptionsDestination
{
    [JsonProperty("permissions")]
    public SlskdOptionsPermissions Permissions { get; set; }
}

public class SlskdOptionsPermissions
{
    [JsonProperty("mode")]
    public string Mode { get; set; }
}

public class SlskdOptionsSoulseek
{
    [JsonProperty("username")]
    public string Username { get; set; }
}

public class SlskdOptionsDirectories
{
    [JsonProperty("downloads")]
    public string Downloads { get; set; }

    [JsonProperty("incomplete")]
    public string Incomplete { get; set; }
}
