using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

/// <summary>
/// A listing of slskd's downloads directory (GET /api/v0/files/downloads/directories/{base64}).
/// With recursive=true every file below the listed directory is returned flat, named by its path
/// relative to that directory.
/// </summary>
public class FilesystemDirectory
{
    [JsonProperty("files")]
    public List<FilesystemFile> Files { get; set; }
}

public class FilesystemFile
{
    [JsonProperty("fullName")]
    public string FullName { get; set; }

    [JsonProperty("length")]
    public long Length { get; set; }
}
