using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class SearchResponseFile
{
    [JsonProperty("attributeCount")]
    public int? AttributeCount { get; set; }

    [JsonProperty("attributes")]
    public List<UserDirectoryFileAttribute> Attributes { get; set; }

    [JsonIgnore]
    private string _fileName;

    [JsonProperty("filename")]
    public string FileName
    {
        get => _fileName;
        set
        {
            _fileName = value;

            if (value == null)
            {
                return;
            }

            var parts = value.Split('\\');

            // File name itself
            Name = parts[^1];

            // Parent folder: Two levels above the file
            ParentFolder = parts.Length > 2
                ? string.Join("\\", parts[^3..^1]) // Two directories above the file
                : parts.Length > 1
                    ? parts[^2] // Single parent folder
                    : null;

            // Full path of the immediate parent directory
            ParentPath = parts.Length > 1
                ? string.Join("\\", parts[..^1]) // Full path excluding the file name
                : null;
        }
    }

    [JsonIgnore]
    public string Name { get; set; }

    [JsonIgnore]
    public string ParentFolder { get; set; }

    [JsonIgnore]
    public string ParentPath { get; set; }

    [JsonProperty("extension")]
    public string Extension { get; set; }

    [JsonProperty("code")]
    public int Code { get; set; }

    [JsonProperty("length")]
    public int Length { get; set; }

    [JsonProperty("size")]
    public int Size { get; set; }

    [JsonProperty("isLocked")]
    public bool? IsLocked { get; set; }

    [JsonProperty("bitDepth")]
    public int? BitDepth { get; set; }

    [JsonProperty("sampleRate")]
    public int? SampleRate { get; set; }

    [JsonProperty("bitRate")]
    public int? BitRate { get; set; }

    [JsonProperty("isVariableBitRate")]
    public bool? IsVariableBitRate { get; set; }
}
