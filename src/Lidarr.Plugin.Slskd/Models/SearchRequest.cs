using Newtonsoft.Json;

namespace NzbDrone.Plugin.Slskd.Models
{
    public class SearchRequest
    {
        public SearchRequest(string searchText, int? searchTimeout)
        {
            SearchText = searchText;
            SearchTimeout = searchTimeout;
        }

        // Gets or sets the search text.
        [JsonProperty("searchText")]
        public string SearchText { get; set; }

        // Gets or sets the maximum number of file results to accept before the search is considered complete. (Default = 10,000).
        [JsonProperty("fileLimit", NullValueHandling = NullValueHandling.Ignore)]
        public int? FileLimit { get; set; }

        // Gets or sets a value indicating whether responses are to be filtered. (Default = true).
        [JsonProperty("filterResponses", NullValueHandling = NullValueHandling.Ignore)]
        public bool? FilterResponses { get; set; }

        // Gets or sets the maximum queue depth a peer may have in order for a response to be processed. (Default = 1000000).
        [JsonProperty("maximumPeerQueueLength", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaximumPeerQueueLength { get; set; }

        // Gets or sets the minimum upload speed a peer must have in order for a response to be processed. (Default = 0).
        [JsonProperty("minimumPeerUploadSpeed", NullValueHandling = NullValueHandling.Ignore)]
        public int? MinimumPeerUploadSpeed { get; set; }

        // Gets or sets the minimum number of files a response must have in order to be processed. (Default = 1).
        [JsonProperty("minimumResponseFileCount", NullValueHandling = NullValueHandling.Ignore)]
        public int? MinimumResponseFileCount { get; set; }

        // Gets or sets the maximum number of search results to accept before the search is considered complete. (Default = 100).
        [JsonProperty("responseLimit", NullValueHandling = NullValueHandling.Ignore)]
        public int? ResponseLimit { get; set; }

        // Gets or sets the search timeout value, in milliseconds, used to determine when the search is complete. (Default = 15000).
        [JsonProperty("searchTimeout", NullValueHandling = NullValueHandling.Ignore)]
        public int? SearchTimeout { get; set; }
    }
}
