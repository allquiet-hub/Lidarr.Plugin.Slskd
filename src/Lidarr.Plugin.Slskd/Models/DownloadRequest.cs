using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models
{
    public class DownloadRequest
    {
        [JsonProperty("filename")]
        public string Filename { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }
    }
}
