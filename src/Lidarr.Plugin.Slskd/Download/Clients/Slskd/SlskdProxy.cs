using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Plugin.Slskd.Helpers;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Core.Download.Clients.Slskd
{
    public interface ISlskdProxy
    {
        bool TestConnectivity(SlskdSettings settings);
        SlskdOptions GetOptions(SlskdSettings settings);
        List<DownloadClientItem> GetQueue(SlskdSettings settings);
        string Download(string searchId, string username, string downloadPath, SlskdSettings settings);
        void RemoveFromQueue(string downloadId, bool deleteData, SlskdSettings settings);
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
            var completedDownloadsPath = GetOptions(settings).Directories.Downloads;
            var downloadItems = new List<DownloadClientItem>();

            // Transform grouped files into DownloadClientItem instances
            foreach (var queue in downloadsQueues)
            {
                foreach (var directory in queue.Directories)
                {
                    // Ensure extensions are filled if missing
                    FileProcessingUtils.EnsureFileExtensions(directory.Files);

                    // Filter valid audio files
                    var audioFiles = directory.Files
                        .Where(file =>
                            !string.IsNullOrEmpty(file.Extension) &&
                            FileProcessingUtils.ValidAudioExtensions.Contains(file.Extension))
                        .ToList();

                    // Skip if no valid audio files
                    if (audioFiles.Count == 0)
                    {
                        continue;
                    }

                    var remainingSize = audioFiles.Sum(file => file.BytesRemaining);
                    var totalSize = audioFiles.Sum(file => file.Size);
                    var currentlyDownloadingFile = FileProcessingUtils.GetCurrentlyDownloadingFile(audioFiles);

                    var userStatus = GetUserStatus(queue.Username, settings);
                    var message = $"Downloaded from user {queue.Username}";
                    var pendingFiles = directory.Files.Where(f => f.TransferState.State != TransferStateEnum.Completed).ToList();

                    if (userStatus.IsOnline)
                    {
                        var userDirectory = GetUserDirectory(queue.Username, directory.Directory, settings);
                        FileProcessingUtils.CombineFilesWithMetadata(audioFiles, userDirectory.Files);
                        if (pendingFiles.Any() && pendingFiles.All(f => f.TransferState == new TransferStates()
                            {
                                State = TransferStateEnum.Queued,
                                Substate = TransferStateEnum.Remotely
                            }))
                        {
                            var position = GetFilePlaceInUserQueue(queue.Username, pendingFiles.First().Id, settings);
                            message = $"User {queue.Username} has queued your download, position {position}";
                        }
                    }
                    else
                    {
                        message = $"User {queue.Username} is offline, cannot get media quality";
                    }

                    var title = FileProcessingUtils.BuildTitle(audioFiles);
                    var downloadId = queue.Username + "\\" + directory.Directory;

                    var downloadItem = new DownloadClientItem
                    {
                        DownloadId = downloadId,
                        Title = title,
                        TotalSize = totalSize,
                        RemainingSize = remainingSize,
                        Status = GetItemStatus(currentlyDownloadingFile.TransferState), // Get status from the first file
                        Message = message, // Message based on the first file
                        OutputPath = new OsPath(Path.Combine(completedDownloadsPath, currentlyDownloadingFile.FirstParentFolder)), // Completed downloads folder + parent folder
                        CanBeRemoved = true
                    };
                    downloadItems.Add(downloadItem);
                }
            }

            return downloadItems;
        }

        private bool IsConnectedLoggedIn(SlskdSettings settings)
        {
            var request = BuildRequest(settings, 1).Resource("/api/v0/application");
            var response = ProcessRequest<Application>(request);

            return response.Server.IsConnected && response.Server.IsLoggedIn;
        }

        private int? GetFilePlaceInUserQueue(string username, string fileId, SlskdSettings settings)
        {
            var filePlaceInQueueRequest = BuildRequest(settings, 1)
                .Resource($"/api/v0/transfers/downloads/{username}/{fileId}/position")
                .Build();
            int? position;
            try
            {
                position = ProcessRequest<int>(filePlaceInQueueRequest);
            }
            catch (Exception)
            {
                throw new DownloadClientException("Error getting file position in queue from user: {0}, fileId: {1}", username, fileId);
            }

            return position;
        }

        private UserDirectory GetUserDirectory(string username, string directory, SlskdSettings settings)
        {
            var userDirectory = new UserDirectoryRequest { DirectoryPath = directory };
            var userDirectoryRequest = BuildRequest(settings, 1)
                .Resource($"/api/v0/users/{username}/directory")
                .Post()
                .Build();
            var json = userDirectory.ToJson();
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

        private UserStatus GetUserStatus(string username, SlskdSettings settings)
        {
            var userStatusRequest = BuildRequest(settings, 1)
                .Resource($"/api/v0/users/{username}/status")
                .Build();
            userStatusRequest.Headers.ContentType = "application/json";
            return ProcessRequest<UserStatus>(userStatusRequest);
        }

        public void RemoveFromQueue(string downloadId, bool deleteData, SlskdSettings settings)
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

            foreach (var file in downloadDirectory.Files)
            {
                CancelUserDownloadFile(username, file.Id, false, settings);
            }

            if (!deleteData)
            {
                return;
            }

            foreach (var file in downloadDirectory.Files)
            {
                CancelUserDownloadFile(username, file.Id, true, settings);
            }

            var base64Directory = FileProcessingUtils.Base64Encode(directoryName);
            var removeDirectoryRequest = BuildRequest(settings, 0.01)
                .Resource($"/api/v0/files/downloads/directories/{base64Directory}")
                .AddQueryParam("remove", true)
                .Build();
            removeDirectoryRequest.Method = HttpMethod.Delete;
            ProcessRequest(removeDirectoryRequest);
        }

        public string Download(string searchId, string username, string downloadPath, SlskdSettings settings)
        {
            var downloadUid = username + "\\" + downloadPath;

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

            var files = userResponse.Files.Where(f => f.ParentPath == downloadPath).ToList();

            downloadList.AddRange(files.Select(file => new DownloadRequest { Filename = file.FileName, Size = file.Size }));
            var downloadRequest = BuildRequest(settings, 0.01)
                .Resource($"/api/v0/transfers/downloads/{username}")
                .Post()
                .Build();
            var json = downloadList.ToJson();
            downloadRequest.Headers.ContentType = "application/json";
            downloadRequest.SetContent(json);
            downloadRequest.ContentSummary = json;
            downloadRequest.RequestTimeout = new TimeSpan(0, 1, 0);
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

        private void CancelUserDownloadFile(string username, string fileId, bool deleteFile, SlskdSettings settings)
        {
            var cancelFileRequest = BuildRequest(settings, 0.01)
                .Resource($"/api/v0/transfers/downloads/{username}/{fileId}")
                .AddQueryParam("remove", deleteFile)
                .Build();
            cancelFileRequest.Method = HttpMethod.Delete;
            ProcessRequest(cancelFileRequest);
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
