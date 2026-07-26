using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// <summary>
        /// First path segment of every destination the plugin asks slskd to download into, relative to
        /// the configured downloads directory. Downloads land in '[downloads]/lidarr/[downloadId]/[album]'
        /// so the output path is known up front instead of being inferred from the remote folder name.
        /// </summary>
        private const string DestinationRoot = "lidarr";

        private const int BatchCacheLimit = 1000;

        private static readonly TimeSpan CapabilityCacheDuration = TimeSpan.FromMinutes(5);

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        // Batch options never change once the batch is created, so they can be cached indefinitely.
        // A null value marks a batch that could not be resolved, to avoid hammering the API.
        private readonly ConcurrentDictionary<string, BatchOptions> _batchOptionsCache = new ();
        private readonly ConcurrentDictionary<string, (DateTime Expiry, bool Supported)> _batchSupportCache = new ();

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
            var response = GetApplication(settings);
            return response?.Server.IsConnected == true && response.Server.IsLoggedIn;
        }

        public SlskdOptions GetOptions(SlskdSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            return ExecuteGet<SlskdOptions>(BuildRequest(settings, "/api/v0/options/"));
        }

        public Application GetApplication(SlskdSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            return ExecuteGet<Application>(BuildRequest(settings, "/api/v0/application/"));
        }

        public bool SupportsBatches(SlskdSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var key = HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase);

            if (_batchSupportCache.TryGetValue(key, out var cached) && cached.Expiry > DateTime.UtcNow)
            {
                return cached.Supported;
            }

            var supported = SlskdCapabilities.SupportsBatches(GetApplication(settings)?.Version);
            _batchSupportCache[key] = (DateTime.UtcNow.Add(CapabilityCacheDuration), supported);

            if (!supported)
            {
                _logger.Debug($"Slskd instance is older than {SlskdCapabilities.BatchesMinimumVersion}, " +
                              "falling back to the legacy download endpoint. Upgrade slskd to let Lidarr " +
                              "control the completed download location.");
            }

            return supported;
        }

        public List<DownloadClientItem> GetQueue(SlskdSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var downloadsQueues = ExecuteGet<List<DownloadsQueue>>(BuildRequest(settings, "/api/v0/transfers/downloads/"));
            if (downloadsQueues == null)
            {
                return new List<DownloadClientItem>();
            }

            // Keep the slskd-side path style: Lidarr may run on a different platform, and the remote path
            // mapping compares the value as reported by the download client.
            var completedDownloadsPath = new OsPath(GetOptions(settings).Directories.Downloads);

            var groups = new Dictionary<string, QueueGroup>(StringComparer.Ordinal);

            foreach (var queue in downloadsQueues)
            {
                foreach (var directory in queue.Directories)
                {
                    FileProcessingUtils.EnsureFileExtensions(directory.Files);
                    var audioFiles = directory.Files.FilterValidAudioFiles().Where(f => !f.Removed).ToList();
                    if (!audioFiles.Any())
                    {
                        continue;
                    }

                    // A directory can hold files belonging to different batches, so partition by batch first
                    foreach (var batch in audioFiles.GroupBy(f => f.BatchId ?? string.Empty, StringComparer.Ordinal))
                    {
                        var files = batch.ToList();
                        var batchDownloadId = ResolveDownloadId(batch.Key, settings);

                        string key;
                        OsPath outputPath;

                        if (batchDownloadId != null)
                        {
                            key = batchDownloadId;
                            outputPath = completedDownloadsPath + DestinationRoot + batchDownloadId;
                        }
                        else
                        {
                            // Legacy layout: slskd decides where the files land, so the album folder is
                            // reconstructed from the remote path. Disc sub-folders (CD1, CD2, ...) are
                            // merged under their parent so the id matches the one computed during search.
                            var canonicalDir = GetCanonicalDirectory(directory.Directory);
                            key = Crc32Hasher.Crc32Base64($"{queue.Username}{canonicalDir}");
                            outputPath = completedDownloadsPath + files[0].FirstParentFolder;
                        }

                        if (!groups.TryGetValue(key, out var group))
                        {
                            groups[key] = group = new QueueGroup(queue.Username, outputPath);
                        }

                        group.Files.AddRange(files);
                    }
                }
            }

            var items = new List<DownloadClientItem>();
            foreach (var (identifier, group) in groups)
            {
                var audioFiles = group.Files;
                var totalSize = audioFiles.Sum(f => f.Size);
                var remainingSize = audioFiles.Sum(f => f.BytesRemaining);
                var averageSpeed = audioFiles
                    .Where(f => f.BytesTransferred > 0)
                    .Select(f => f.AverageSpeed)
                    .DefaultIfEmpty(1)
                    .Average();
                var message = $"Downloaded from user {group.Username}";

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
                    OutputPath = group.OutputPath,
                    CanBeRemoved = true,
                    CanMoveFiles = true,
                };

                if (status == DownloadItemStatus.Downloading && averageSpeed > 0 && totalSize > 0)
                {
                    downloadClientItem.RemainingTime = TimeSpan.FromSeconds(totalSize / averageSpeed);
                }

                items.Add(downloadClientItem);
            }

            return items;
        }

        public string Download(string searchId, string username, string downloadPath, SlskdSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var request = BuildRequest(settings, $"/api/v0/searches/{searchId}/")
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

            // Match files at the exact path or in disc sub-folders under the path
            var files = userResponse.Files.Where(f =>
                f.FileName == downloadPath ||
                f.ParentPath == downloadPath ||
                (f.ParentPath?.StartsWith(downloadPath + "\\", StringComparison.OrdinalIgnoreCase) == true)).ToList();

            var audioFiles = files.FilterValidAudioFiles();
            if (!audioFiles.Any())
            {
                throw new DownloadClientException($"No files found for path: {downloadPath}");
            }

            // For single-file releases the parser hands over the file path rather than the folder;
            // the identifier is always computed on the folder so it matches ReleaseInfo.Guid.
            var albumPath = audioFiles.Any(f => string.Equals(f.FileName, downloadPath, StringComparison.OrdinalIgnoreCase))
                ? FileProcessingUtils.GetParentPath(downloadPath)
                : downloadPath;

            var identifier = Crc32Hasher.Crc32Base64($"{username}{albumPath}");

            if (SupportsBatches(settings))
            {
                EnqueueBatches(searchId, username, albumPath, audioFiles, identifier, settings);
            }
            else
            {
                var downloadRequests = audioFiles.Select(file => new DownloadRequest { Filename = file.FileName, Size = file.Size }).ToList();
                Execute(BuildRequest(settings, $"/api/v0/transfers/downloads/{username}/").Post(), downloadRequests.ToJson());
            }

            return identifier;
        }

        public void RemoveFromQueue(string downloadId, bool deleteData, SlskdSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var queues = ExecuteGet<List<DownloadsQueue>>(BuildRequest(settings, "/api/v0/transfers/downloads/"));
            if (queues == null)
            {
                _logger.Warn($"Could not retrieve download queue when attempting to remove download ID: {downloadId}");
                return;
            }

            // Collect every transfer that belongs to this download ID. Batched downloads are matched on
            // the batch external ID; for legacy ones, multiple disc sub-folders map to the same album ID.
            var matchingFiles = new List<(string Username, DirectoryFile File)>();
            var matchingDirectories = new List<DownloadDirectory>();
            var isBatched = false;

            foreach (var queue in queues)
            {
                foreach (var directory in queue.Directories)
                {
                    // Match on the individual transfers rather than the whole directory: it can also hold
                    // files enqueued outside of Lidarr, and those must not be cancelled.
                    var batchFiles = directory.Files
                        .Where(f => ResolveDownloadId(f.BatchId, settings) == downloadId)
                        .ToList();

                    if (batchFiles.Any())
                    {
                        matchingFiles.AddRange(batchFiles.Select(file => (queue.Username, file)));
                        matchingDirectories.Add(directory);
                        isBatched = true;
                        continue;
                    }

                    var canonicalDir = GetCanonicalDirectory(directory.Directory);
                    if (Crc32Hasher.Crc32Base64($"{queue.Username}{canonicalDir}") == downloadId)
                    {
                        matchingFiles.AddRange(directory.Files.Select(file => (queue.Username, file)));
                        matchingDirectories.Add(directory);
                    }
                }
            }

            if (matchingFiles.Count == 0)
            {
                _logger.Warn($"No user or directory found with matching hash for download ID: {downloadId}");
                return;
            }

            foreach (var (username, file) in matchingFiles)
            {
                CancelUserDownloadFile(username, file.Id, false, settings);

                if (!deleteData)
                {
                    continue;
                }

                WaitForFileCompleted(username, file.Id, settings);
                CancelUserDownloadFile(username, file.Id, true, settings);
            }

            if (!deleteData)
            {
                return;
            }

            string directoryToDelete;

            if (isBatched)
            {
                // Everything the plugin enqueued for this release lives under a single known folder
                directoryToDelete = $"{DestinationRoot}/{downloadId}";
            }
            else
            {
                // For multi-disc albums, delete the shared parent; for single albums, the directory itself.
                var firstDir = matchingDirectories[0].Directory;
                var firstDirName = firstDir?.Split('\\').LastOrDefault() ?? string.Empty;
                directoryToDelete = (matchingDirectories.Count > 1 || FileProcessingUtils.IsDiscFolder(firstDirName))
                    ? FileProcessingUtils.GetParentPath(firstDir)
                    : firstDir;
            }

            DeleteDownloadDirectory(directoryToDelete, settings);
        }

        // Download Helpers

        /// <summary>
        /// Enqueues one batch per remote directory, all sharing the same destination prefix, which pins
        /// where the completed files land regardless of the 'transfers.download.destination.subdirectory'
        /// expression configured in slskd. The download ID is carried by the destination itself: the
        /// external ID is sent as well, but slskd 0.26.0 never exposes it back through the API.
        /// </summary>
        private void EnqueueBatches<T>(string searchId, string username, string albumPath, List<T> audioFiles, string identifier, SlskdSettings settings)
            where T : SlskdFile
        {
            var albumFolder = FileProcessingUtils.SanitizePathSegment(albumPath.Split('\\').LastOrDefault());
            var destinationPrefix = $"{DestinationRoot}/{identifier}";

            foreach (var group in audioFiles.GroupBy(f => f.ParentPath ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                // Files of a batch are placed flat in its destination, so disc sub-folders need their own
                // batch to survive; the relative remote path is appended to keep the structure intact.
                var relativeSegments = group.Key.Length > albumPath.Length
                    ? group.Key[albumPath.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries)
                    : Array.Empty<string>();

                var segments = new List<string> { destinationPrefix, albumFolder };
                segments.AddRange(relativeSegments.Select(FileProcessingUtils.SanitizePathSegment));

                var body = new EnqueueBatchRequest
                {
                    SearchId = Guid.TryParse(searchId, out _) ? searchId : null,
                    Username = username,
                    Files = group.Select(file => new DownloadRequest { Filename = file.FileName, Size = file.Size }).ToList(),
                    Options = new BatchOptions
                    {
                        Destination = string.Join('/', segments.Where(s => !string.IsNullOrEmpty(s))),
                        ExternalId = identifier
                    }
                };

                var response = ExecutePost<EnqueueBatchResponse>(
                    BuildRequest(settings, "/api/v0/transfers/downloads/batches/"), body.ToJson());

                foreach (var failure in response?.Failures ?? new List<EnqueueBatchFailure>())
                {
                    _logger.Warn($"Slskd refused to enqueue '{failure.Filename}': {failure.Message}");
                }

                if (response?.Batch?.Id != null)
                {
                    // Seed the cache so the first queue poll does not need to look the batch up
                    CacheBatchOptions(response.Batch.Id, body.Options);
                }
            }
        }

        /// <summary>
        /// Returns the download ID of a Lidarr-owned batch, or null when the batch is unknown or was
        /// created outside of Lidarr (in which case the legacy path reconstruction is used instead).
        /// </summary>
        private string ResolveDownloadId(string batchId, SlskdSettings settings)
        {
            if (string.IsNullOrEmpty(batchId))
            {
                return null;
            }

            if (!_batchOptionsCache.TryGetValue(batchId, out var options))
            {
                try
                {
                    var batch = ExecuteGet<Batch>(BuildRequest(settings, $"/api/v0/transfers/downloads/batches/{batchId}/"));
                    options = batch?.Options;
                }
                catch (HttpException httpException)
                {
                    _logger.Debug($"Could not retrieve batch '{batchId}': {httpException.Message}");
                    options = null;
                }

                CacheBatchOptions(batchId, options);
            }

            return GetDownloadIdFromDestination(options?.Destination);
        }

        /// <summary>
        /// Extracts the download ID from a batch destination of the form 'lidarr/[downloadId]/[album]'.
        /// The ID is read back from the destination rather than from BatchOptions.ExternalId because
        /// slskd 0.26.0 accepts the external ID on enqueue but never echoes it back through the API.
        /// Returns null for batches created outside of Lidarr.
        /// </summary>
        private static string GetDownloadIdFromDestination(string destination)
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                return null;
            }

            var segments = destination.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            return segments.Length >= 2 && segments[0].Equals(DestinationRoot, StringComparison.OrdinalIgnoreCase)
                ? segments[1]
                : null;
        }

        private void CacheBatchOptions(string batchId, BatchOptions options)
        {
            // Bounded to keep long-running instances from accumulating entries indefinitely
            if (_batchOptionsCache.Count >= BatchCacheLimit)
            {
                _batchOptionsCache.Clear();
            }

            _batchOptionsCache[batchId] = options;
        }

        private static string GetCanonicalDirectory(string directory)
        {
            var dirName = directory?.Split('\\').LastOrDefault() ?? string.Empty;
            return FileProcessingUtils.IsDiscFolder(dirName)
                ? FileProcessingUtils.GetParentPath(directory)
                : directory;
        }

        private void DeleteDownloadDirectory(string directory, SlskdSettings settings)
        {
            // slskd resolves the path against the downloads directory; base64 has to be escaped because
            // its alphabet includes characters that are meaningful in a URL path.
            var encodedDirectory = Uri.EscapeDataString(FileProcessingUtils.Base64Encode(directory));
            var resource = $"/api/v0/files/downloads/directories/{encodedDirectory}/";

            try
            {
                var response = Execute(BuildRequest(settings, resource));
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpException httpException)
            {
                if (httpException.Response.StatusCode != HttpStatusCode.NotFound)
                {
                    throw new DownloadClientException($"Error getting directory information: {directory}");
                }

                _logger.Warn($"Directory '{directory}' does not exist on disk. Skipping deletion.");
                return;
            }

            var deleteRequest = BuildRequest(settings, resource);
            deleteRequest.Method = HttpMethod.Delete;

            try
            {
                Execute(deleteRequest);
                _logger.Info($"Successfully deleted directory '{directory}'.");
            }
            catch (HttpException httpException)
            {
                _logger.Error($"Failed to delete directory '{directory}'.");
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

        private T ExecutePost<T>(HttpRequestBuilder requestBuilder, string content)
            where T : new()
        {
            var response = Execute(requestBuilder.Post(), content);
            return string.IsNullOrWhiteSpace(response.Content) ? new T() : Json.Deserialize<T>(response.Content);
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
            var cancelRequest = BuildRequest(settings, $"/api/v0/transfers/downloads/{username}/{fileId}/")
                .AddQueryParam("remove", deleteFile);
            cancelRequest.Method = HttpMethod.Delete;

            Execute(cancelRequest);
            _logger.Trace($"Canceled and removed file '{fileId}' for user '{username}'. DeleteFile: {deleteFile}");
        }

        private void WaitForFileCompleted(string username, string fileId, SlskdSettings settings)
        {
            var stopwatch = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(10);

            while (stopwatch.Elapsed < timeout)
            {
                var fileRequest = BuildRequest(settings, $"/api/v0/transfers/downloads/{username}/{fileId}/");
                fileRequest.RateLimit = _rateLimit;

                var file = ExecuteGet<DirectoryFile>(fileRequest);
                if (file?.TransferState?.State == TransferStates.Completed)
                {
                    _logger.Trace($"File '{fileId}' for user '{username}' is marked as completed.");
                    return;
                }
            }

            _logger.Warn($"Timeout waiting for file '{fileId}' to complete for user '{username}'.");
        }

        private sealed class QueueGroup
        {
            public QueueGroup(string username, OsPath outputPath)
            {
                Username = username;
                OutputPath = outputPath;
            }

            public string Username { get; }
            public OsPath OutputPath { get; }
            public List<DirectoryFile> Files { get; } = new ();
        }
    }
}
