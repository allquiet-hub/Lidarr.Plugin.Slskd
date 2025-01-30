using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using NLog;
using NzbDrone.Common.Crypto;
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
            var response = ExecuteGet<Application>(BuildRequest(settings, "/api/v0/application"));
            return response?.Server.IsConnected == true && response.Server.IsLoggedIn;
        }

        public SlskdOptions GetOptions(SlskdSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            return ExecuteGet<SlskdOptions>(BuildRequest(settings, "/api/v0/options"));
        }

        public List<DownloadClientItem> GetQueue(SlskdSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

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
                        continue;
                    }

                    var identifier = Crc32Hasher.Crc32Base64($"{queue.Username}{directory.Directory}");

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

                    var downloadClientItem = new DownloadClientItem
                    {
                        DownloadId = identifier,
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
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var request = BuildRequest(settings, $"/api/v0/searches/{searchId}")
                .AddQueryParam("includeResponses", true);

            var result = ExecuteGet<SearchResult>(request);
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
            Execute(downloadRequest, downloadJson);

            var identifier = Crc32Hasher.Crc32Base64($"{username}{files[0].ParentPath}");
            return identifier;
        }

        public void RemoveFromQueue(string downloadId, bool deleteData, SlskdSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var queues = ExecuteGet<List<DownloadsQueue>>(BuildRequest(settings, "/api/v0/transfers/downloads"));
            var username = string.Empty;
            DownloadDirectory downloadDirectory = null;
            foreach (var queue in queues)
            {
                foreach (var directory in
                         queue.Directories.Where(directory =>
                             Crc32Hasher.Crc32Base64($"{queue.Username}{directory.Directory}") == downloadId))
                {
                    username = queue.Username;
                    downloadDirectory = directory;
                }
            }

            if (downloadDirectory == null)
            {
                _logger.Warn($"No user or directory found with matching hash.");
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
            var base64Directory = FileProcessingUtils.Base64Encode(downloadDirectory.Directory);
            var directoryCheckRequest = BuildRequest(settings, $"/api/v0/files/downloads/directories/{base64Directory}");
            HttpResponse response;

            try
            {
                response = Execute(directoryCheckRequest);
            }
            catch (HttpException httpException)
            {
                if (httpException.Response.StatusCode != HttpStatusCode.NotFound)
                {
                    throw new DownloadClientException($"Error getting directory information: {downloadDirectory.Directory}");
                }

                _logger.Warn($"Directory '{downloadDirectory.Directory}' does not exist on disk. Skipping deletion.");
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
                _logger.Info($"Successfully deleted directory '{downloadDirectory.Directory}'.");
            }
            catch (HttpException httpException)
            {
                _logger.Error($"Failed to delete directory '{downloadDirectory.Directory}'.");
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

        private T ExecuteGet<T>(HttpRequestBuilder requestBuilder)
            where T : new()
        {
            var response = _httpClient.Get(requestBuilder.Build());
            return Json.Deserialize<T>(response.Content);
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

        private void CancelUserDownloadFile(string username, string fileId, bool deleteFile, SlskdSettings settings)
        {
            var cancelRequest = BuildRequest(settings, $"/api/v0/transfers/downloads/{username}/{fileId}")
                .AddQueryParam("remove", deleteFile);
            cancelRequest.Method = HttpMethod.Delete;

            Execute(cancelRequest);
            _logger.Trace($"Canceled and removed file '{fileId}' for user '{username}'. DeleteFile: {deleteFile}");
        }

        private void WaitForFileCompleted(string username, string fileId, SlskdSettings settings)
        {
            var stopwatch = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(10);
            var shouldRateLimit = false;

            while (stopwatch.Elapsed < timeout)
            {
                var fileRequest = BuildRequest(settings, $"/api/v0/transfers/downloads/{username}/{fileId}");
                if (shouldRateLimit)
                {
                    fileRequest.RateLimit = _rateLimit;
                }

                var file = ExecuteGet<DirectoryFile>(fileRequest);
                if (file.TransferState.State == TransferStates.Completed)
                {
                    _logger.Trace($"File '{fileId}' for user '{username}' is marked as completed.");
                    return;
                }

                shouldRateLimit = true;
            }

            _logger.Warn($"Timeout waiting for file '{fileId}' to complete for user '{username}'.");
        }
    }
}
