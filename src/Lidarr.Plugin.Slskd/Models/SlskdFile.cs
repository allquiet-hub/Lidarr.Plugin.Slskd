using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models;

public class SlskdFile
{
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

            Name = parts[^1];

            ParentPath = parts.Length > 1
                ? string.Join("\\", parts[..^1])
                : null;
        }
    }

    [JsonIgnore]
    public string Name { get; set; }

    [JsonIgnore]
    public string ParentPath { get; set; }

    [JsonProperty("extension")]
    public string Extension { get; set; }

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("bitDepth")]
    public int? BitDepth { get; set; }

    [JsonProperty("sampleRate")]
    public int? SampleRate { get; set; }

    [JsonProperty("bitRate")]
    public int? BitRate { get; set; }

    [JsonProperty("isVariableBitRate")]
    public bool? IsVariableBitRate { get; set; }
}
