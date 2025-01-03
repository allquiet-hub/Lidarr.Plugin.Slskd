using System;
using System.Collections.Generic;

namespace NzbDrone.Plugin.Slskd.Models
{
    public class SearchResult
    {
        public string Id { get; set; }
        public bool IsComplete { get; set; }
        public int FileCount { get; set; }
        public int LockedFileCount { get; set; }
        public string SearchText { get; set; }
        public string State { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public int Token { get; set; }
        public int ResponseCount { get; set; }
        public List<SearchResponse> Responses { get; set; }
    }
}
