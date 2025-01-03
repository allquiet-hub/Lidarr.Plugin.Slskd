using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Core.Download.Clients.Slskd
{
    public interface ISlskdProxy
    {
        bool TestConnectivity(SlskdSettings settings);
        SlskdOptions GetOptions(SlskdSettings settings);
        List<DownloadClientItem> GetQueue(SlskdSettings settings);
        string Download(string searchId, string username, string downloadPath, SlskdSettings settings);
        void RemoveFromQueue(string downloadId, SlskdSettings settings);
    }

    public class SlskdProxy : ISlskdProxy
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public SlskdProxy(IHttpClient httpClient = null, Logger logger = null)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public bool TestConnectivity(SlskdSettings settings)
        {
            return IsConnectedLoggedIn(settings);
        }

        private bool IsConnectedLoggedIn(SlskdSettings settings)
        {
            var request = BuildRequest(settings).Resource("/api/v0/application");
            var response = ProcessRequest<Application>(request);

            return response.Server.IsConnected && response.Server.IsLoggedIn;
        }

        public SlskdOptions GetOptions(SlskdSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            var request = BuildRequest(settings).Resource("/api/v0/options");
            var response = ProcessRequest<SlskdOptions>(request);

            return response;
        }

        public List<DownloadClientItem> GetQueue(SlskdSettings settings)
        {
            var downloadsRequest = BuildRequest(settings).Resource("/api/v0/transfers/downloads");
            var downloadsQueues = ProcessRequest<List<DownloadsQueue>>(downloadsRequest);

            // Valid audio file extensions (without the dot)
            var validExtensions = new HashSet<string>
            {
                "flac", "alac", "wav", "ape", "ogg", "aac", "mp3", "wma"
            };

            // Fetch completed downloads folder from options
            // GetOptions(settings).Directories.Downloads;
            var completedDownloadsPath = @"U:\downloads\slskd\complete";

            var downloadItems = new List<DownloadClientItem>();

            // Transform grouped files into DownloadClientItem instances
            foreach (var queue in downloadsQueues)
            {
                foreach (var directory in queue.Directories)
                {
                    // Group currently downloading files by ParentFolder
                    var groupedDownloadingFiles = directory.Files
                        .GroupBy(file => file.ParentPath);
                    var currentlyDownloadingFile = directory.Files.First();
                    long remainingSize = 0;
                    long totalSize = 0;

                    foreach (var group in groupedDownloadingFiles)
                    {
                        remainingSize = group.Sum(file => file.BytesRemaining);
                        totalSize = group.Sum(file => file.Size);
                        currentlyDownloadingFile = group
                            .OrderBy(file => file.RequestedAt)
                            .ThenBy(file => file.EnqueuedAt)
                            .ThenBy(file => file.StartedAt)
                            .ThenBy(file => file.EndedAt)
                            .Last();
                    }

                    var userDirectoryRequest = BuildRequest(settings)
                        .Resource($"/api/v0/searches/{queue.Username}/directory")
                        .AddFormParameter("directory", directory.Directory);
                    var userDirectoryResult = ProcessRequest<UserDirectory>(userDirectoryRequest);

                    // Group files by ParentFolder
                    var groupedFiles = userDirectoryResult.Files
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

                        var firstFile = files.First();

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
                        var titleBuilder = new StringBuilder(firstFile.ParentFolder.Replace('\\', ' ')).Append(' ');
                        if (isSingleFileInParentDirectory)
                        {
                            titleBuilder.Append(firstFile.Name.Replace($".{firstFile.Extension}", "")).Append(' ');
                        }

                        titleBuilder.AppendJoin(' ', codec, bitRate, sampleRateAndDepth, isVariableBitRate);

                        var downloadItem = new DownloadClientItem
                        {
                            DownloadId = Guid.NewGuid().ToString(),
                            Title = titleBuilder.ToString().Trim(), // Use the parent folder as the title
                            TotalSize = totalSize,
                            RemainingSize = remainingSize,
                            Status = GetItemStatus(currentlyDownloadingFile.TransferState), // Get status from the first file
                            Message = $"Downloaded from {currentlyDownloadingFile.Username}", // Message based on the first file
                            OutputPath = new OsPath(Path.Combine(completedDownloadsPath, currentlyDownloadingFile.ParentFolder)) // Completed downloads folder + parent folder
                        };

                        downloadItems.Add(downloadItem);
                    }
                }
            }

            return downloadItems;
        }

        public void RemoveFromQueue(string downloadId, SlskdSettings settings)
        {
            var request = BuildRequest(settings)
                .Resource($"/api/v0/transfers/downloads/{downloadId}");

            ProcessRequest(request);
        }

        public string Download(string searchId, string username, string downloadPath, SlskdSettings settings)
        {
            var downloadUid = $"{username}|{downloadPath}";

            var request = BuildRequest(settings)
                .Resource($"/api/v0/searches/{searchId}")
                .AddQueryParam("includeResponses", true);
            var result = ProcessRequest<SearchResult>(request);

            var downloadList = new List<DownloadRequest>();
            if (result?.Responses == null || result.Responses.Count == 0)
            {
                throw new DownloadClientException("Error adding item to Slskd: {0}", downloadUid);
            }

            var userResponse = result.Responses.First(r => r.Username == username);
            if (userResponse.Files == null || userResponse.Files.Count == 0)
            {
                throw new DownloadClientException("Error adding item to Slskd: {0}", downloadUid);
            }

            var files = userResponse.Files.Where(f => f.FileName.StartsWith(downloadPath)).ToList();

            downloadList.AddRange(files.Select(file => new DownloadRequest { Filename = file.FileName, Size = file.Size }));
            var downloadRequest = BuildRequest(settings)
                .Resource($"/api/v0/transfers/downloads/{username}")
                .Post()
                .Build();
            var json = JsonConvert.SerializeObject(downloadList);
            downloadRequest.Headers.ContentType = "application/json";
            downloadRequest.SetContent(json);
            downloadRequest.ContentSummary = json;
            var downloadResult = string.Empty;

            try
            {
                downloadResult = ProcessRequest(downloadRequest);
            }
            catch (Exception)
            {
                throw new DownloadClientException("Error adding item to Slskd: {0}; {1}", downloadUid, downloadResult);
            }

            _logger.Trace("Downloading item {0}", downloadUid);
            return downloadUid;
        }

        private static HttpRequestBuilder BuildRequest(SlskdSettings settings)
        {
            var requestBuilder = new HttpRequestBuilder(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase)
            {
                LogResponseContent = true
            };

            // Add API key header
            requestBuilder.Accept(HttpAccept.Json);
            requestBuilder.SetHeader("X-API-Key", settings.ApiKey);

            return requestBuilder;
        }

        private TResult ProcessRequest<TResult>(HttpRequestBuilder requestBuilder)
            where TResult : new()
        {
            var responseContent = ProcessRequest(requestBuilder);
            return Json.Deserialize<TResult>(responseContent);
        }

        private string ProcessRequest(HttpRequestBuilder requestBuilder)
        {
            var request = requestBuilder.Build();

            HttpResponse response;
            try
            {
                response = _httpClient.Execute(request);
            }
            catch (HttpException ex)
            {
                throw new DownloadClientException("Failed to connect to Slskd, check your settings.", ex);
            }
            catch (WebException ex)
            {
                throw new DownloadClientException("Failed to connect to Slskd, please check your settings.", ex);
            }

            return response.Content;
        }

        private string ProcessRequest(HttpRequest httpRequest)
        {
            HttpResponse response;
            try
            {
                response = _httpClient.Execute(httpRequest);
            }
            catch (HttpException ex)
            {
                throw new DownloadClientException("Failed to connect to Slskd, check your settings.", ex);
            }
            catch (WebException ex)
            {
                throw new DownloadClientException("Failed to connect to Slskd, please check your settings.", ex);
            }

            return response.Content;
        }

        private static DownloadItemStatus GetItemStatus(TransferStates states)
        {
            switch (states.State)
            {
                case TransferStateEnum.Completed:
                    switch (states.Substate)
                    {
                        case TransferStateEnum.Succeeded:
                            return DownloadItemStatus.Completed;
                        case TransferStateEnum.Cancelled:
                        case TransferStateEnum.Errored:
                            return DownloadItemStatus.Warning;
                        default:
                            return DownloadItemStatus.Warning;
                    }

                case TransferStateEnum.Requested:
                case TransferStateEnum.Queued:
                    return DownloadItemStatus.Queued;
                case TransferStateEnum.InProgress:
                    return DownloadItemStatus.Downloading;
                default:
                    return DownloadItemStatus.Warning;
            }
        }
    }
}
