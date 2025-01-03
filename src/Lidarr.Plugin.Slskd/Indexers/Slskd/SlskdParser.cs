using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Core.Indexers.Slskd
{
    public class SlskdParser : IParseIndexerResponse
    {
        private readonly ProviderDefinition _definition;
        private readonly SlskdIndexerSettings _settings;
        private readonly TimeSpan _rateLimit;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public SlskdParser(ProviderDefinition definition, SlskdIndexerSettings settings, TimeSpan rateLimit, IHttpClient httpClient, Logger logger)
        {
            _definition = definition;
            _settings = settings;
            _rateLimit = rateLimit;
            _httpClient = httpClient;
            _logger = logger;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            var torrentInfos = new List<ReleaseInfo>();

            var jsonResponse = new HttpResponse<SearchResult>(indexerResponse.HttpResponse);
            var searchRequest =
                JsonConvert.DeserializeObject<SearchRequest>(indexerResponse.HttpRequest.ContentSummary);
            var searchResult = jsonResponse.Resource;
            var searchId = searchResult.Id;

            // Wait for the search to complete
            var isCompleted = false;
            var stopwatch = Stopwatch.StartNew();
            var timeout = searchRequest.SearchTimeout + 50000; // Wait an extra 2 seconds, the search should be complete
            var request = RequestBuilder()
                .Resource($"api/v0/searches/{searchId}")
                .AddQueryParam("includeResponses", "false")
                .Build();

            while (!isCompleted && stopwatch.ElapsedMilliseconds < timeout)
            {
                searchResult = new HttpResponse<SearchResult>(_httpClient.Execute(request)).Resource;
                if (searchResult == null)
                {
                    throw new Exception("Failed to query the search result.", null);
                }

                isCompleted = searchResult.IsComplete;
                Debug.WriteLine($"Search Status: {searchResult.State}; found {searchResult.ResponseCount} responses.");
            }

            stopwatch.Stop();

            // Ensure the search completed successfully
            if (!isCompleted)
            {
                throw new TimeoutException("Search did not complete within the specified timeout.");
            }

            request = RequestBuilder()
                .Resource($"api/v0/searches/{searchId}")
                .AddQueryParam("includeResponses", "true")
                .Build();
            searchResult = new HttpResponse<SearchResult>(_httpClient.Execute(request)).Resource;
            if (searchResult == null)
            {
                throw new Exception("Failed to query the search result.", null);
            }

            torrentInfos.AddRange(ToReleaseInfo(searchResult));

            // order by date
            return torrentInfos
                .OrderByDescending(o => o.Size)
                .ToArray();
        }

        private IList<ReleaseInfo> ToReleaseInfo(SearchResult searchResult)
        {
            // Valid audio file extensions (without the dot)
            var validExtensions = new HashSet<string>
            {
                "flac", "alac", "wav", "ape", "ogg", "aac", "mp3", "wma"
            };

            var releaseInfos = new List<ReleaseInfo>();

            foreach (var response in searchResult.Responses)
            {
                var groupedFiles = response.Files
                    .GroupBy(file => file.ParentPath);

                foreach (var group in groupedFiles)
                {
                    var files = group.ToList();
                    var isSingleFileInParentDirectory = files.Count == 1;

                    // Ensure extensions are filled if missing
                    foreach (var file in files)
                    {
                        if (!string.IsNullOrEmpty(file.Extension))
                        {
                            continue;
                        }

                        var lastDotIndex = file.Name.LastIndexOf('.');
                        if (lastDotIndex >= 0)
                        {
                            file.Extension = file.Name[(lastDotIndex + 1) ..].ToLower();
                        }
                    }

                    // Filter valid audio files
                    var audioFiles = files
                        .Where(file => !string.IsNullOrEmpty(file.Extension) && validExtensions.Contains(file.Extension))
                        .ToList();

                    // Skip if no valid audio files
                    if (audioFiles.Count == 0)
                    {
                        continue;
                    }

                    var firstFile = audioFiles.First();

                    // Determine codec
                    var codec = audioFiles.Select(f => f.Extension).Distinct().Count() == 1
                        ? firstFile.Extension.ToUpper(System.Globalization.CultureInfo.InvariantCulture)
                        : null;

                    // Determine bit rate
                    string bitRate = null;
                    if (audioFiles.All(f => f.BitRate.HasValue && f.BitRate == firstFile.BitRate))
                    {
                        bitRate = $"{firstFile.BitRate}kbps";
                    }

                    // Determine sample rate and bit depth
                    string sampleRateAndDepth = null;
                    if (audioFiles.All(f => f.SampleRate.HasValue && f.BitDepth.HasValue))
                    {
                        var sampleRate = firstFile.SampleRate / 1000.0; // Convert Hz to kHz
                        var bitDepth = firstFile.BitDepth;
                        sampleRateAndDepth = $"{bitDepth}bit {sampleRate?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}kHz";
                    }

                    // Determine VBR/CBR
                    string isVariableBitRate = null;
                    if (audioFiles.All(f => f.IsVariableBitRate.HasValue && f.IsVariableBitRate.Value))
                    {
                        isVariableBitRate = "VBR";
                    }
                    else if (audioFiles.All(f => f.IsVariableBitRate.HasValue && !f.IsVariableBitRate.Value))
                    {
                        isVariableBitRate = "CBR";
                    }

                    // Build the title
                    var titleBuilderFirst = new StringBuilder(firstFile.FirstParentFolder.Replace('\\', ' ')).Append(' ');
                    if (isSingleFileInParentDirectory)
                    {
                        titleBuilderFirst.Append(firstFile.Name.Replace($".{firstFile.Extension}", "")).Append(' ');
                    }

                    titleBuilderFirst.AppendJoin(' ', codec, bitRate, sampleRateAndDepth, isVariableBitRate);

                    var titleBuilderSecond = new StringBuilder(firstFile.SecondParentFolder.Replace('\\', ' ')).Append(' ');
                    if (isSingleFileInParentDirectory)
                    {
                        titleBuilderSecond.Append(firstFile.Name.Replace($".{firstFile.Extension}", "")).Append(' ');
                    }

                    titleBuilderSecond.AppendJoin(' ', codec, bitRate, sampleRateAndDepth, isVariableBitRate);

                    // Create ReleaseInfo object
                    releaseInfos.Add(new ReleaseInfo
                    {
                        Guid = Guid.NewGuid().ToString(),
                        Title = titleBuilderFirst.ToString().Trim(),
                        Album = firstFile.FirstParentFolder,
                        DownloadUrl = isSingleFileInParentDirectory
                            ? firstFile.FileName
                            : firstFile.ParentPath,
                        InfoUrl = $"{_settings.BaseUrl}searches/{searchResult.Id}",
                        Size = files.Sum(f => f.Size),
                        Source = response.Username,
                        Origin = searchResult.Id,
                        DownloadProtocol = nameof(SlskdDownloadProtocol)
                    });
                    releaseInfos.Add(new ReleaseInfo
                    {
                        Guid = Guid.NewGuid().ToString(),
                        Title = titleBuilderSecond.ToString().Trim(),
                        Album = firstFile.SecondParentFolder,
                        DownloadUrl = isSingleFileInParentDirectory
                            ? firstFile.FileName
                            : firstFile.ParentPath,
                        InfoUrl = $"{_settings.BaseUrl}searches/{searchResult.Id}",
                        Size = files.Sum(f => f.Size),
                        Source = response.Username,
                        Origin = searchResult.Id,
                        DownloadProtocol = nameof(SlskdDownloadProtocol)
                    });
                }
            }

            return releaseInfos;
        }

        private HttpRequestBuilder RequestBuilder()
        {
            return new HttpRequestBuilder(_settings.BaseUrl)
                .Accept(HttpAccept.Json)
                .WithRateLimit(1)
                .SetHeader("X-API-Key", _settings.ApiKey);
        }
    }
}
