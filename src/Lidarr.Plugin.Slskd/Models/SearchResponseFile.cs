using System.Collections.Generic;
using Newtonsoft.Json;
using NzbDrone.Plugin.Slskd.Interfaces;

namespace NzbDrone.Plugin.Slskd.Models;

public class SearchResponseFile : SlskdFile
{
    [JsonProperty("attributeCount")]
    public int? AttributeCount { get; set; }

    [JsonProperty("attributes")]
    public List<UserDirectoryFileAttribute> Attributes { get; set; }

    [JsonProperty("code")]
    public int Code { get; set; }

    [JsonProperty("length")]
    public int Length { get; set; }

    [JsonProperty("isLocked")]
    public bool? IsLocked { get; set; }
}
