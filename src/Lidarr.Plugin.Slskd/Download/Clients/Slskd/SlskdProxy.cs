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
        private TimeSpan _rateLimit;

        public SlskdProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _rateLimit = TimeSpan.FromMilliseconds(500);
        }

        // Core Public Methods
        public bool TestConnectivity(SlskdSettings settings)
        {
            var response = ExecuteGet<Application>(settings, BuildRequest(settings, "/api/v0/application"));
            return response?.Server.IsConnected == true && response.Server.IsLoggedIn;
        }

        public SlskdOptions GetOptions(SlskdSettings settings)
        {
            return ExecuteGet<SlskdOptions>(settings, BuildRequest(settings, "/api/v0/options"));
        }

        public List<DownloadClientItem> GetQueue(SlskdSettings settings)
        {
            var downloadsQueues = ExecuteGet<List<DownloadsQueue>>(settings, BuildRequest(settings, "/api/v0/transfers/downloads"));
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
                        continue;
                    }

                    var totalSize = audioFiles.Sum(file => file.Size);
                    var remainingSize = audioFiles.Sum(file => file.BytesRemaining);
                    var averageSpeed = audioFiles
                        .Where(file => file.BytesTransferred > 0)
                        .Select(file => file.AverageSpeed)
                        .DefaultIfEmpty(1) // Handles case where no files meet the condition
                        .Average();
                    var message = $"Downloaded from user {queue.Username}";

                    var (status, statusMessage) = FileProcessingUtils.GetQueuedFilesStatus(audioFiles);
                    if (statusMessage != null)
                    {
                        message = statusMessage;
                    }

                    var downloadPath = audioFiles.Count == 1 ? audioFiles[0]?.FileName : directory.Directory;
                    var downloadClientItem = new DownloadClientItem
                    {
                        DownloadId = $"{queue.Username}\\{downloadPath}",
                        Title = FileProcessingUtils.BuildTitle(audioFiles),
                        TotalSize = totalSize,
                        RemainingSize = remainingSize,
                        Status = status,
                        Message = message,
                        OutputPath = new OsPath(Path.Combine(
                            completedDownloadsPath,
                            audioFiles.First().FirstParentFolder)),
                        CanBeRemoved = true,
                    };
                    if (status == DownloadItemStatus.Downloading && averageSpeed > 0 && totalSize > 0)
                    {
                        downloadClientItem.RemainingTime = TimeSpan.FromSeconds(totalSize / averageSpeed);
                    }

                    items.Add(downloadClientItem);
                }
            }

            return items;
        }

        public string Download(string searchId, string username, string downloadPath, SlskdSettings settings)
        {
            var request = BuildRequest(settings, $"/api/v0/searches/{searchId}")
                .AddQueryParam("includeResponses", true);

            var result = ExecuteGet<SearchResult>(settings, request);
            if (result?.Responses == null)
            {
                throw new DownloadClientException($"Error adding item to Slskd: Search result not found for {searchId}");
            }

            if (!result.Responses.Any())
            {
                throw new DownloadClientException($"Error adding item to Slskd: No responses received for {searchId}");
            }

            var userResponse = result.Responses.FirstOrDefault(r => r.Username == username);
            if (userResponse?.Files == null)
            {
                throw new DownloadClientException($"Error adding item to Slskd: {searchId}");
            }

            var files = userResponse.Files.Where(f => f.FileName == downloadPath || f.ParentPath == downloadPath).ToList();
            var audioFiles = files.FilterValidAudioFiles();
            if (!audioFiles.Any())
            {
                throw new DownloadClientException($"No files found for path: {downloadPath}");
            }

            var downloadRequests = audioFiles.Select(file => new DownloadRequest { Filename = file.FileName, Size = file.Size }).ToList();
            var downloadJson = downloadRequests.ToJson();

            var downloadRequest = BuildRequest(settings, $"/api/v0/transfers/downloads/{username}")
                .Post();
            Execute(settings, downloadRequest, downloadJson);
            return $"{username}\\{downloadPath}";
        }

        public void RemoveFromQueue(string downloadId, bool deleteData, SlskdSettings settings)
        {
            var split = downloadId.Split('\\');
            if (split.Length < 2)
            {
                throw new ArgumentException($@"Invalid downloadId format: {downloadId}", nameof(downloadId));
            }

            var username = split[0];
            var idEndsWithExtension =
                FileProcessingUtils.ValidAudioExtensions.Any(ext => downloadId.EndsWith($".{ext}"));
            var directoryPath = string.Join("\\", idEndsWithExtension ? split[1..^2] : split[1..]);
            var directoryName = idEndsWithExtension ? split[^2] : split[^1];

            // Fetch the downloads queue for the user
            DownloadsQueue downloadsQueue;
            try
            {
                downloadsQueue = ExecuteGet<DownloadsQueue>(settings, BuildRequest(settings, $"/api/v0/transfers/downloads/{username}"));
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
            HttpResponse response;

            try
            {
                response = Execute(settings, directoryCheckRequest);
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
                Execute(settings, deleteRequest);
                _logger.Info($"Successfully deleted directory '{directoryName}'.");
            }
            catch (HttpException httpException)
            {
                _logger.Error($"Failed to delete directory '{directoryName}'.");
                _logger.Trace(httpException);
            }
        }

        // HTTP Request Helpers
        private static HttpRequestBuilder BuildRequest(SlskdSettings settings, string resource)
        {
            return new HttpRequestBuilder(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase)
                .Resource(resource)
                .Accept(HttpAccept.Json)
                .SetHeader("X-API-Key", settings.ApiKey);
        }

        private T ExecuteGet<T>(SlskdSettings settings, HttpRequestBuilder requestBuilder)
            where T : new()
        {
            var response = _httpClient.Get(requestBuilder.Build());
            return Json.Deserialize<T>(response.Content);
        }

        private HttpResponse Execute(SlskdSettings settings, HttpRequestBuilder requestBuilder, string content = null)
        {
            var request = requestBuilder.Build();
            if (content != null)
            {
                request.Headers.ContentType = "application/json";
                request.SetContent(content);
            }

            return _httpClient.Execute(request);
        }

        private void CancelUserDownloadFile(string username, string fileId, bool deleteFile, SlskdSettings settings)
        {
            var cancelRequest = BuildRequest(settings, $"/api/v0/transfers/downloads/{username}/{fileId}")
                .AddQueryParam("remove", deleteFile);
            cancelRequest.Method = HttpMethod.Delete;

            Execute(settings, cancelRequest);
            _logger.Trace($"Canceled and removed file '{fileId}' for user '{username}'. DeleteFile: {deleteFile}");
        }

        private void WaitForFileCompleted(string username, string fileId, SlskdSettings settings)
        {
            var stopwatch = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(10);

            while (stopwatch.Elapsed < timeout)
            {
                var fileRequest = BuildRequest(settings, $"/api/v0/transfers/downloads/{username}/{fileId}");
                fileRequest.RateLimit = _rateLimit;
                var file = ExecuteGet<DirectoryFile>(settings, fileRequest);

                if (file.TransferState.State != TransferStates.Completed)
                {
                    continue;
                }

                _logger.Trace($"File '{fileId}' for user '{username}' is marked as completed.");
                return;
            }

            _logger.Warn($"Timeout waiting for file '{fileId}' to complete for user '{username}'.");
        }
    }
}
