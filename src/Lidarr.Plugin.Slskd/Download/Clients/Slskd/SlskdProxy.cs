using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            var response = ExecuteGet<Application>(BuildRequest(settings, "/api/v0/application"));
            return response?.Server.IsConnected == true && response.Server.IsLoggedIn;
        }

        public SlskdOptions GetOptions(SlskdSettings settings)
        {
            return ExecuteGet<SlskdOptions>(BuildRequest(settings, "/api/v0/options"));
        }

        public List<DownloadClientItem> GetQueue(SlskdSettings settings)
        {
            var downloadsQueues = ExecuteGet<List<DownloadsQueue>>(BuildRequest(settings, "/api/v0/transfers/downloads"));
            if (downloadsQueues == null)
            {
                return new List<DownloadClientItem>();
            }

            var completedDownloadsPath = GetOptions(settings).Directories.Downloads;
            var items = new List<DownloadClientItem>();
            foreach (var queue in downloadsQueues)
            {
                foreach (var directory in queue.Directories)
                {
                    FileProcessingUtils.EnsureFileExtensions(directory.Files);
                    var audioFiles = directory.Files.FilterValidAudioFiles();

                    if (!audioFiles.Any())
                    {
                        return null;
                    }

                    var currentlyDownloadingFile = FileProcessingUtils.GetCurrentlyDownloadingFile(audioFiles);
                    var totalSize = audioFiles.Sum(file => file.Size);
                    var remainingSize = audioFiles.Sum(file => file.BytesRemaining);

                    var message = $"Downloaded from user {queue.Username}";

                    if (audioFiles.All(f => f.TransferState.State == TransferStateEnum.Completed))
                    {
                        var userStatus = GetUserStatus(queue.Username, settings);
                        if (userStatus.IsOnline)
                        {
                            var pendingFiles = directory.Files.Where(f => f.TransferState.State != TransferStateEnum.Completed).ToList();
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
                    }

                    items.Add(new DownloadClientItem
                    {
                        DownloadId = $"{queue.Username}\\{directory.Directory}",
                        Title = FileProcessingUtils.BuildTitle(audioFiles),
                        TotalSize = totalSize,
                        RemainingSize = remainingSize,
                        Status = GetItemStatus(currentlyDownloadingFile.TransferState),
                        Message = message,
                        OutputPath = new OsPath(Path.Combine(
                            completedDownloadsPath,
                            currentlyDownloadingFile.FirstParentFolder)),
                        CanBeRemoved = true,
                    });
                }
            }

            return items;
        }

        public string Download(string searchId, string username, string downloadPath, SlskdSettings settings)
        {
            var request = BuildRequest(settings, $"/api/v0/searches/{searchId}")
                .AddQueryParam("includeResponses", true);

            var result = ExecuteGet<SearchResult>(request);
            if (result?.Responses == null)
            {
                throw new DownloadClientException($"Error adding item to Slskd: {searchId}");
            }

            var userResponse = result.Responses.FirstOrDefault(r => r.Username == username);
            if (userResponse?.Files == null)
            {
                throw new DownloadClientException($"Error adding item to Slskd: {searchId}");
            }

            var files = userResponse.Files.Where(f => f.ParentPath == downloadPath).ToList();
            var audioFiles = files.FilterValidAudioFiles();
            if (!audioFiles.Any())
            {
                throw new DownloadClientException($"No files found for path: {downloadPath}");
            }

            var downloadRequests = audioFiles.Select(file => new DownloadRequest { Filename = file.FileName, Size = file.Size }).ToList();
            var downloadJson = downloadRequests.ToJson();

            var downloadRequest = BuildRequest(settings, $"/api/v0/transfers/downloads/{username}").Post().Build();
            downloadRequest.RequestTimeout = TimeSpan.FromMinutes(5);
            Execute(downloadRequest, downloadJson);
            return $"{username}\\{downloadPath}";
        }

        public void RemoveFromQueue(string downloadId, bool deleteData, SlskdSettings settings)
        {
            var split = downloadId.Split('\\');
            var username = split[0];
            var directoryPath = string.Join("\\", split.Skip(1));
            var directoryName = split[^1];

            // Fetch the downloads queue for the user
            DownloadsQueue downloadsQueue = null;
            try
            {
                downloadsQueue = ExecuteGet<DownloadsQueue>(BuildRequest(settings, $"/api/v0/transfers/downloads/{username}"));
            }
            catch (HttpException httpException)
            {
                if (httpException.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.Warn($"User '{username}' not present in download queue. Skipping deletion.");
                    return;
                }

                throw new DownloadClientException($"Error getting directory information: {directoryName}");
            }

            var downloadDirectory = downloadsQueue?.Directories.FirstOrDefault(dir => dir.Directory.StartsWith(directoryPath));
            if (downloadDirectory == null)
            {
                _logger.Warn($"Directory '{directoryPath}' not found in the queue for user '{username}'.");
                return;
            }

            foreach (var file in downloadDirectory.Files)
            {
                // Cancel the file download
                CancelUserDownloadFile(username, file.Id, false, settings);

                if (!deleteData)
                {
                    continue;
                }

                // Wait for the file to be marked as completed before proceeding
                WaitForFileCompleted(username, file.Id, settings);

                // Remove the file if required
                CancelUserDownloadFile(username, file.Id, true, settings);
            }

            if (!deleteData)
            {
                return;
            }

            // Use the API to check if the directory exists based on HTTP response code
            var base64Directory = FileProcessingUtils.Base64Encode(directoryName);
            var directoryCheckRequest = BuildRequest(settings, $"/api/v0/files/downloads/directories/{base64Directory}");
            HttpResponse response = null;

            try
            {
                response = Execute(directoryCheckRequest);
            }
            catch (HttpException httpException)
            {
                if (httpException.Response.StatusCode != HttpStatusCode.NotFound)
                {
                    throw new DownloadClientException($"Error getting directory information: {directoryName}");
                }

                _logger.Warn($"Directory '{directoryName}' does not exist on disk. Skipping deletion.");
                return;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return;
            }

            var deleteRequest = BuildRequest(settings, $"/api/v0/files/downloads/directories/{base64Directory}");
            deleteRequest.Method = HttpMethod.Delete;
            try
            {
                Execute(deleteRequest);
                _logger.Info($"Successfully deleted directory '{directoryName}'.");
            }
            catch (HttpException httpException)
            {
                _logger.Error($"Failed to delete directory '{directoryName}'.");
                _logger.Trace(httpException);
            }
        }

        private static HttpRequestBuilder BuildRequest(SlskdSettings settings, string resource)
        {
            return new HttpRequestBuilder(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase)
                .WithRateLimit(0.2)
                .Resource(resource)
                .Accept(HttpAccept.Json)
                .SetHeader("X-API-Key", settings.ApiKey);
        }

        private void CancelUserDownloadFile(string username, string fileId, bool deleteFile, SlskdSettings settings)
        {
            var cancelRequest = BuildRequest(settings, $"/api/v0/transfers/downloads/{username}/{fileId}")
                .AddQueryParam("remove", deleteFile);
            cancelRequest.Method = HttpMethod.Delete;

            try
            {
                Execute(cancelRequest);
                _logger.Trace($"Canceled and removed file '{fileId}' for user '{username}'. DeleteFile: {deleteFile}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to cancel or remove file '{fileId}' for user '{username}'.");
            }
        }

        private void WaitForFileCompleted(string username, string fileId, SlskdSettings settings)
        {
            var stopwatch = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(10); // Increased timeout for safety

            while (stopwatch.Elapsed < timeout)
            {
                var fileRequest = BuildRequest(settings, $"/api/v0/transfers/downloads/{username}/{fileId}").WithRateLimit(0.5);
                var file = ExecuteGet<DirectoryFile>(fileRequest);

                if (file.TransferState.State != TransferStateEnum.Completed)
                {
                    continue;
                }

                _logger.Trace($"File '{fileId}' for user '{username}' is marked as completed.");
                return;
            }

            _logger.Warn($"Timeout waiting for file '{fileId}' to complete for user '{username}'.");
        }

        private UserStatus GetUserStatus(string username, SlskdSettings settings)
        {
            var userStatus = ExecuteGet<UserStatus>(BuildRequest(settings, $"/api/v0/users/{username}/status"));
            return userStatus;
        }

        private int GetFilePlaceInUserQueue(string username, string fileId, SlskdSettings settings)
        {
            var request = BuildRequest(settings, $"/api/v0/transfers/downloads/{username}/{fileId}/position");
            var response = Execute(request);
            var position = Convert.ToInt32(response.Content);
            return position;
        }

        private UserDirectory GetUserDirectory(string username, string directory, SlskdSettings settings)
        {
            var request = BuildRequest(settings, $"/api/v0/users/{username}/directory");
            var directoryRequest = new UserDirectoryRequest { DirectoryPath = directory };
            var userDirectory = ExecutePost<UserDirectory>(request, directoryRequest.ToJson());
            foreach (var file in userDirectory.Files)
            {
                file.FileName = Path.Combine(userDirectory.DirectoryPath, file.FileName);
            }

            return userDirectory;
        }

        private T ExecuteGet<T>(HttpRequestBuilder requestBuilder)
            where T : new()
        {
            var httpResponse = _httpClient.Get<T>(requestBuilder.Build());
            return httpResponse.Resource;
        }

        private T ExecutePost<T>(HttpRequestBuilder requestBuilder, string content = null)
            where T : new()
        {
            var request = requestBuilder.Build();
            if (content != null)
            {
                request.Headers.ContentType = "application/json";
                request.SetContent(content);
            }

            var httpResponse = _httpClient.Post<T>(request);
            return httpResponse.Resource;
        }

        private HttpResponse Execute(HttpRequestBuilder requestBuilder, string content = null)
        {
            var request = requestBuilder.Build();
            if (content != null)
            {
                request.Headers.ContentType = "application/json";
                request.SetContent(content);
            }

            return _httpClient.Execute(request);
        }

        private HttpResponse Execute(HttpRequest request, string content = null)
        {
            if (content != null)
            {
                request.Headers.ContentType = "application/json";
                request.SetContent(content);
            }

            return _httpClient.Execute(request);
        }

        private static DownloadItemStatus GetItemStatus(TransferStates states)
        {
            return states.State switch
            {
                TransferStateEnum.Completed when states.Substate == TransferStateEnum.Succeeded => DownloadItemStatus.Completed,
                TransferStateEnum.Requested or TransferStateEnum.Queued => DownloadItemStatus.Queued,
                TransferStateEnum.Initializing or TransferStateEnum.InProgress => DownloadItemStatus.Downloading,
                _ => DownloadItemStatus.Warning
            };
        }
    }
}
