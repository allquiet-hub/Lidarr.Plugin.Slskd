using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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

        private static readonly HashSet<string> ValidAudioExtensions = new ()
        {
            "flac", "alac", "wav", "ape", "ogg", "aac", "mp3", "wma"
        };

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
            var request = BuildRequest(settings, 1).Resource("/api/v0/application");
            var response = ProcessRequest<Application>(request);

            return response.Server.IsConnected && response.Server.IsLoggedIn;
        }

        public SlskdOptions GetOptions(SlskdSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            var request = BuildRequest(settings, 1).Resource("/api/v0/options");
            var response = ProcessRequest<SlskdOptions>(request);

            return response;
        }

        public List<DownloadClientItem> GetQueue(SlskdSettings settings)
        {
            var downloadsRequest = BuildRequest(settings, 1).Resource("/api/v0/transfers/downloads");
            var downloadsQueues = ProcessRequest<List<DownloadsQueue>>(downloadsRequest);

            // Fetch completed downloads folder from options
            // GetOptions(settings).Directories.Downloads;
            var completedDownloadsPath = @"U:\downloads\slskd\complete";

            var downloadItems = new List<DownloadClientItem>();

            // Transform grouped files into DownloadClientItem instances
            foreach (var queue in downloadsQueues)
            {
                foreach (var directory in queue.Directories)
                {
                    // Ensure extensions are filled if missing
                    foreach (var file in directory.Files)
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

                    // Group currently downloading files by SecondParentFolder
                    var groupedDownloadingFiles = directory.Files
                        .Where(file => file.Extension != null && ValidAudioExtensions.Contains(file.Extension))
                        .GroupBy(file => file.ParentPath);
                    var currentlyDownloadingFile = directory.Files.First();
                    long remainingSize = 0;
                    long totalSize = 0;

                    foreach (var group in groupedDownloadingFiles)
                    {
                        remainingSize = group.Sum(file => file.BytesRemaining);
                        totalSize = group.Sum(file => file.Size);

// Group files by their transfer state
                        var groupedByState = group.GroupBy(file => file.TransferState.State)
                            .ToDictionary(g => g.Key, g => g.ToList());

                        // Prioritize the downloading state (InProgress)
                        if (groupedByState.TryGetValue(TransferStateEnum.InProgress, out var inProgressFiles))
                        {
                            // If there are any files in progress, select the most recent one based on your ordering rules
                            currentlyDownloadingFile = inProgressFiles
                                .OrderBy(file => file.RequestedAt)
                                .ThenBy(file => file.EnqueuedAt)
                                .ThenBy(file => file.StartedAt)
                                .FirstOrDefault(); // No need for Last since InProgress is a priority
                        }
                        else if (groupedByState.TryGetValue(TransferStateEnum.Completed, out var completedFiles))
                        {
                            // If no files are in progress, select the most recently completed file
                            currentlyDownloadingFile = completedFiles
                                .OrderBy(file => file.RequestedAt)
                                .ThenBy(file => file.EnqueuedAt)
                                .ThenBy(file => file.StartedAt)
                                .ThenBy(file => file.EndedAt)
                                .LastOrDefault(); // Choose the most recent completed file
                        }
                        else
                        {
                            // If no InProgress or Completed files exist, handle fallback
                            currentlyDownloadingFile = group
                                .OrderBy(file => file.RequestedAt)
                                .ThenBy(file => file.EnqueuedAt)
                                .ThenBy(file => file.StartedAt)
                                .ThenBy(file => file.EndedAt)
                                .LastOrDefault();
                        }
                    }

                    var userDirectory = GetUserDirectory(queue.Username, directory.Directory, settings);

                    // Group files by SecondParentFolder
                    var groupedFiles = userDirectory.Files
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
                            .Where(file => !string.IsNullOrEmpty(file.Extension) && ValidAudioExtensions.Contains(file.Extension))
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

                        // Determine sample rate and bitDepth
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
                        var titleBuilder = new StringBuilder((firstFile.SecondParentFolder ?? firstFile.FirstParentFolder).Replace('\\', ' ')).Append(' ');
                        if (isSingleFileInParentDirectory)
                        {
                            titleBuilder.Append(firstFile.Name.Replace($".{firstFile.Extension}", "")).Append(' ');
                        }

                        titleBuilder.AppendJoin(' ', codec, bitRate, sampleRateAndDepth, isVariableBitRate);
                        var downloadId = Path.Combine(queue.Username, directory.Directory);

                        var downloadItem = new DownloadClientItem
                        {
                            DownloadId = downloadId,
                            Title = titleBuilder.ToString().Trim(), // Use the parent folder as the title
                            TotalSize = totalSize,
                            RemainingSize = remainingSize,
                            Status = GetItemStatus(currentlyDownloadingFile.TransferState), // Get status from the first file
                            Message = $"Downloaded from {queue.Username}", // Message based on the first file
                            OutputPath = new OsPath(Path.Combine(completedDownloadsPath, directory.Files.First().FirstParentFolder)) // Completed downloads folder + parent folder
                        };

                        downloadItems.Add(downloadItem);
                    }
                }
            }

            return downloadItems;
        }

        private UserDirectory GetUserDirectory(string username, string directory, SlskdSettings settings)
        {
            var userDirectory = new UserDirectoryRequest { DirectoryPath = directory };
            var userDirectoryRequest = BuildRequest(settings, 1)
                .Resource($"/api/v0/users/{username}/directory")
                .Post()
                .Build();
            var json = JsonConvert.SerializeObject(userDirectory);
            userDirectoryRequest.Headers.ContentType = "application/json";
            userDirectoryRequest.SetContent(json);
            userDirectoryRequest.ContentSummary = json;
            UserDirectory userDirectoryResult = null;

            try
            {
                userDirectoryResult = ProcessRequest<UserDirectory>(userDirectoryRequest);
            }
            catch (Exception)
            {
                throw new DownloadClientException("Error getting download information from client: {0}", userDirectoryResult);
            }

            foreach (var file in userDirectoryResult.Files)
            {
                file.FileName = Path.Combine(userDirectoryResult.DirectoryPath, file.FileName);
            }

            return userDirectoryResult;
        }

        public void RemoveFromQueue(string downloadId, SlskdSettings settings)
        {
            var split = downloadId.Split('\\');
            var username = split[0];
            var directoryPath = downloadId.Split('\\', 2)[1];
            var directoryName = split[^1];
            var downloadsRequest = BuildRequest(settings, 1).Resource($"/api/v0/transfers/downloads/{username}");
            var downloadQueue = ProcessRequest<DownloadsQueue>(downloadsRequest);
            var downloadDirectory = downloadQueue.Directories.FirstOrDefault(q => q.Directory.StartsWith(directoryPath));

            if (downloadDirectory == null || downloadDirectory.Files.Count == 0)
            {
                return;
            }

            foreach (var directoryFile in downloadDirectory.Files)
            {
                var removeFileRequest = BuildRequest(settings, 0.01)
                    .Resource($"/api/v0/transfers/downloads/{username}/{directoryFile.Id}")
                    .AddQueryParam("remove", true)
                    .Build();
                removeFileRequest.Method = HttpMethod.Delete;
                ProcessRequest(removeFileRequest);
            }

            var base64Directory = Base64Encode(directoryName);
            var removeDirectoryRequest = BuildRequest(settings, 0.01)
                .Resource($"/api/v0/files/downloads/directories/{base64Directory}")
                .AddQueryParam("remove", true)
                .Build();
            removeDirectoryRequest.Method = HttpMethod.Delete;
            ProcessRequest(removeDirectoryRequest);
        }

        private static string Base64Encode(string plainText)
        {
            var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainTextBytes);
        }

        public string Download(string searchId, string username, string downloadPath, SlskdSettings settings)
        {
            var downloadUid = Path.Combine(username, downloadPath);

            var request = BuildRequest(settings, 1)
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
            var downloadRequest = BuildRequest(settings, 0.01)
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

        private static HttpRequestBuilder BuildRequest(SlskdSettings settings, double rateLimitSeconds)
        {
            var requestBuilder = new HttpRequestBuilder(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase)
            {
                LogResponseContent = true
            };

            // Add API key header
            requestBuilder.Accept(HttpAccept.Json);
            requestBuilder.SetHeader("X-API-Key", settings.ApiKey);
            requestBuilder.WithRateLimit(rateLimitSeconds);

            return requestBuilder;
        }

        private TResult ProcessRequest<TResult>(HttpRequestBuilder requestBuilder)
            where TResult : new()
        {
            var responseContent = ProcessRequest(requestBuilder);
            return Json.Deserialize<TResult>(responseContent);
        }

        private TResult ProcessRequest<TResult>(HttpRequest httpRequest)
            where TResult : new()
        {
            var responseContent = ProcessRequest(httpRequest);
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
                        case TransferStateEnum.Rejected:
                            return DownloadItemStatus.Warning;
                        default:
                            return DownloadItemStatus.Warning;
                    }

                case TransferStateEnum.Requested:
                case TransferStateEnum.Queued:
                    return DownloadItemStatus.Queued;
                case TransferStateEnum.Initializing:
                case TransferStateEnum.InProgress:
                    return DownloadItemStatus.Downloading;
                default:
                    return DownloadItemStatus.Warning;
            }
        }
    }
}
