using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using NLog;
using NzbDrone.Common.Crypto;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
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

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

        private static readonly TimeSpan RemovalDrainTimeout = TimeSpan.FromSeconds(15);

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        // A batch never changes once created, so entries can be cached indefinitely. A null value marks
        // a batch slskd positively does not know (404), for which the legacy grouping is the correct
        // identity; transient lookup failures are deliberately never cached, because answering with the
        // legacy identity while the batch still exists would change the download id mid-flight.
        private readonly ConcurrentDictionary<string, CachedBatch> _batchCache = new ();
        private readonly ConcurrentDictionary<string, (DateTime Expiry, bool Supported)> _batchSupportCache = new ();

        public SlskdProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
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

                        if (!TryResolveDownloadId(batch.Key, settings, out var batchDownloadId))
                        {
                            // The batch exists but could not be looked up right now. Reporting its files
                            // under the fallback identity would change the download id mid-flight, which
                            // reads to Lidarr as the tracked download vanishing and a foreign one
                            // appearing. Leaving them out for one poll is invisible in comparison.
                            _logger.Debug($"Batch '{batch.Key}' could not be resolved, leaving its files out of this queue poll");
                            continue;
                        }

                        string key;
                        OsPath outputPath;
                        string titleBase = null;
                        int? expectedFiles = null;

                        if (batchDownloadId != null)
                        {
                            key = batchDownloadId;
                            outputPath = completedDownloadsPath + DestinationRoot + batchDownloadId;

                            // The destination folder is named "Artist - Album" at enqueue time precisely
                            // so the queue can hand Lidarr a parseable title: parsing is the only route
                            // by which a tracked download gets mapped back to its album.
                            titleBase = GetBatchAlbumFolder(batch.Key);

                            if (_batchCache.TryGetValue(batch.Key, out var cachedBatch))
                            {
                                expectedFiles = cachedBatch?.ExpectedFiles;
                            }
                        }
                        else
                        {
                            // Legacy layout: slskd decides where the files land, so the album folder is
                            // reconstructed from the remote path. Disc sub-folders (CD1, CD2, ...) are
                            // merged under their parent so the id matches the one computed during search.
                            var canonicalDir = GetCanonicalDirectory(directory.Directory);
                            key = ReleaseIdentifier.Compute($"{queue.Username}{canonicalDir}");
                            outputPath = completedDownloadsPath + files[0].FirstParentFolder;
                        }

                        if (!groups.TryGetValue(key, out var group))
                        {
                            groups[key] = group = new QueueGroup(queue.Username, outputPath, titleBase);
                        }

                        group.Files.AddRange(files);
                        group.AddExpectedFiles(expectedFiles);
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

                // The queue only shows transfers that still exist: one aborted and then removed (or
                // expired through slskd's retention) leaves the remaining files looking like a complete,
                // successful download, and Lidarr would import a partial album. The enqueue counts are
                // the memory of what the release was supposed to contain.
                if (status == DownloadItemStatus.Completed && group.ExpectedFiles > audioFiles.Count)
                {
                    status = DownloadItemStatus.Failed;
                    statusMessage = $"Only {audioFiles.Count} of {group.ExpectedFiles} enqueued files are still in the slskd queue, " +
                                    "the others were removed before completing";
                }

                if (statusMessage != null)
                {
                    message = statusMessage;
                }

                var downloadClientItem = new DownloadClientItem
                {
                    DownloadId = identifier,
                    Title = FileProcessingUtils.BuildTitle(audioFiles, group.TitleBase),
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

        public string Download(string searchId, string username, string downloadPath, string identifier, string albumTitle, SlskdSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (identifier.IsNullOrWhiteSpace())
            {
                throw new DownloadClientException($"Release has no identifier, cannot track the download: {downloadPath}");
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
            var files = userResponse.Files.Where(f => BelongsToRelease(f, downloadPath)).ToList();

            var audioFiles = files.FilterValidAudioFiles();
            if (!audioFiles.Any())
            {
                throw new DownloadClientException($"No files found for path: {downloadPath}");
            }

            // For single-file releases the parser hands over the file path rather than the folder
            var albumPath = audioFiles.Any(f => string.Equals(f.FileName, downloadPath, StringComparison.OrdinalIgnoreCase))
                ? FileProcessingUtils.GetParentPath(downloadPath)
                : downloadPath;

            if (SupportsBatches(settings))
            {
                EnqueueBatches(searchId, username, albumPath, audioFiles, identifier, albumTitle, settings);
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
                        .Where(f => TryResolveDownloadId(f.BatchId, settings, out var id) && id == downloadId)
                        .ToList();

                    if (batchFiles.Any())
                    {
                        matchingFiles.AddRange(batchFiles.Select(file => (queue.Username, file)));
                        matchingDirectories.Add(directory);
                        isBatched = true;
                        continue;
                    }

                    var canonicalDir = GetCanonicalDirectory(directory.Directory);
                    if (ReleaseIdentifier.Compute($"{queue.Username}{canonicalDir}") == downloadId)
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

            // Cancel everything first and only then wait: a transfer takes time to reach a terminal
            // state after its cancellation, and waiting per file serialises those transitions into
            // minutes for a large album, all spent inside the request that asked for the removal.
            // Cancelled this way they all transition concurrently, bounded by one shared deadline.
            foreach (var (username, file) in matchingFiles)
            {
                CancelUserDownloadFile(username, file.Id, false, settings);
            }

            if (!deleteData)
            {
                return;
            }

            // slskd will not let go of a file until its transfer is terminal
            WaitForTransfersCompleted(matchingFiles, settings);

            foreach (var (username, file) in matchingFiles)
            {
                CancelUserDownloadFile(username, file.Id, true, settings);
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

        /// <summary>
        /// Fetches the raw YAML configuration file. Requires an API key with the administrator role and
        /// 'remote_configuration' enabled in slskd; both are reported as an authorization error.
        /// </summary>
        public string GetOptionsYaml(SlskdSettings settings)
        {
            var content = _httpClient.Get(BuildRequest(settings, "/api/v0/options/yaml").Build()).Content;

            // The endpoint serves the file as a JSON-encoded string
            return content?.TrimStart().StartsWith('"') == true
                ? Newtonsoft.Json.JsonConvert.DeserializeObject<string>(content)
                : content;
        }

        /// <summary>
        /// Asks slskd itself whether the YAML parses into a valid configuration, returning its error
        /// text or null when valid. Anything written with SaveOptionsYaml must pass through here first:
        /// the file is hand-maintained and an unparseable write would take the whole instance down on
        /// its next restart.
        /// </summary>
        public string ValidateOptionsYaml(string yaml, SlskdSettings settings)
        {
            try
            {
                Execute(BuildRequest(settings, "/api/v0/options/yaml/validate").Post(), yaml.ToJson());
                return null;
            }
            catch (HttpException httpException) when (httpException.Response?.StatusCode == HttpStatusCode.BadRequest)
            {
                return httpException.Response.Content ?? "slskd rejected the configuration";
            }
        }

        public void SaveOptionsYaml(string yaml, SlskdSettings settings)
        {
            var request = BuildRequest(settings, "/api/v0/options/yaml").Build();
            request.Method = HttpMethod.Put;
            request.Headers.ContentType = "application/json";
            request.SetContent(yaml.ToJson());
            _httpClient.Execute(request);
        }

        public int CountActiveDownloads(SlskdSettings settings)
        {
            var queues = ExecuteGet<List<DownloadsQueue>>(BuildRequest(settings, "/api/v0/transfers/downloads/"));

            return queues?
                .SelectMany(q => q.Directories)
                .SelectMany(d => d.Files)
                .Count(f => !f.Removed && f.TransferState?.State != TransferStates.Completed) ?? 0;
        }

        public void Restart(SlskdSettings settings)
        {
            var request = BuildRequest(settings, "/api/v0/application/").Build();
            request.Method = HttpMethod.Put;
            _httpClient.Execute(request);
        }

        // Download Helpers

        /// <summary>
        /// Enqueues one batch per remote directory, all sharing the same destination prefix, which pins
        /// where the completed files land regardless of the 'transfers.download.destination.subdirectory'
        /// expression configured in slskd. The download ID is carried by the destination itself: the
        /// external ID is sent as well, but slskd 0.26.0 never exposes it back through the API.
        /// </summary>
        private void EnqueueBatches<T>(string searchId, string username, string albumPath, List<T> audioFiles, string identifier, string albumTitle, SlskdSettings settings)
            where T : SlskdFile
        {
            // Lidarr identifies what it imported largely from this folder name, so it is named after the
            // album that was grabbed rather than after the remote folder, which belongs to the sharer and
            // for a lone track is usually just the artist.
            var albumFolder = FileProcessingUtils.SanitizePathSegment(albumTitle)
                              ?? FileProcessingUtils.SanitizePathSegment(albumPath.Split('\\').LastOrDefault());
            var destinationPrefix = $"{DestinationRoot}/{identifier}";
            var enqueued = new List<string>();

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

                EnqueueBatchResponse response;

                try
                {
                    response = ExecutePost<EnqueueBatchResponse>(
                        BuildRequest(settings, "/api/v0/transfers/downloads/batches/"), body.ToJson());
                }
                catch (HttpException httpException) when (httpException.Response?.StatusCode == HttpStatusCode.InternalServerError)
                {
                    // The request itself was well formed, otherwise slskd would have rejected it with a
                    // 400, so a server error here means it could not reach the peer holding the files
                    if (enqueued.Any())
                    {
                        _logger.Warn($"Enqueue failed partway through for '{identifier}', discarding the batches already sent");
                        DiscardBatches(enqueued, settings);
                    }

                    throw new SlskdPeerUnavailableException(
                        $"Slskd could not reach user {username}: {httpException.Response?.Content}", httpException);
                }

                foreach (var failure in response?.Failures ?? new List<EnqueueBatchFailure>())
                {
                    _logger.Warn($"Slskd refused to enqueue '{failure.Filename}': {failure.Message}");
                }

                if (response?.Batch?.Id != null)
                {
                    enqueued.Add(response.Batch.Id);

                    // Seed the cache so the first queue poll does not need to look the batch up
                    CacheBatch(response.Batch.Id, new CachedBatch(body.Options, body.Files.Count));
                }
            }
        }

        /// <summary>
        /// Cancels transfers from batches that were accepted before a later one failed, so a release is
        /// never left half enqueued.
        /// </summary>
        private void DiscardBatches(IEnumerable<string> batchIds, SlskdSettings settings)
        {
            foreach (var batchId in batchIds)
            {
                try
                {
                    var batch = ExecuteGet<Batch>(BuildRequest(settings, $"/api/v0/transfers/downloads/batches/{batchId}/"));

                    foreach (var transfer in batch?.Transfers ?? new List<DirectoryFile>())
                    {
                        CancelUserDownloadFile(batch.Username, transfer.Id, true, settings);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, $"Could not discard batch '{batchId}'");
                }
            }
        }

        /// <summary>
        /// Resolves the download ID of a Lidarr-owned batch. A null ID with a true return means the
        /// files positively do not belong to a Lidarr batch (never batched, or slskd does not know the
        /// batch) and the legacy path reconstruction applies. False means the lookup failed transiently
        /// and no identity can be assigned right now: nothing is cached, so the next poll retries.
        /// </summary>
        private bool TryResolveDownloadId(string batchId, SlskdSettings settings, out string downloadId)
        {
            downloadId = null;

            if (string.IsNullOrEmpty(batchId))
            {
                return true;
            }

            if (_batchCache.TryGetValue(batchId, out var cached))
            {
                downloadId = GetDownloadIdFromDestination(cached?.Options?.Destination);
                return true;
            }

            Batch batch;

            try
            {
                batch = ExecuteGet<Batch>(BuildRequest(settings, $"/api/v0/transfers/downloads/batches/{batchId}/"));
            }
            catch (HttpException httpException) when (httpException.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                CacheBatch(batchId, null);
                return true;
            }
            catch (HttpException httpException)
            {
                _logger.Debug($"Could not retrieve batch '{batchId}': {httpException.Message}");
                return false;
            }

            CacheBatch(batchId, batch == null ? null : new CachedBatch(batch.Options, batch.Transfers?.Count));
            downloadId = GetDownloadIdFromDestination(batch?.Options?.Destination);
            return true;
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

        private void CacheBatch(string batchId, CachedBatch batch)
        {
            // Bounded to keep long-running instances from accumulating entries indefinitely
            if (_batchCache.Count >= BatchCacheLimit)
            {
                _batchCache.Clear();
            }

            _batchCache[batchId] = batch;
        }

        /// <summary>
        /// Decides whether a file is part of the release being grabbed.
        ///
        /// Only disc sub-folders count as part of the same release, matching how the search groups them.
        /// Sharers often keep alternate encodings beside the originals ("[album]\AAC", "[album]\MP3"),
        /// and those were offered as separate releases of their own: pulling them in would download the
        /// same album several times over.
        /// </summary>
        private static bool BelongsToRelease(SlskdFile file, string downloadPath)
        {
            if (string.Equals(file.FileName, downloadPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.ParentPath, downloadPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = downloadPath + "\\";
            if (file.ParentPath?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true)
            {
                return false;
            }

            var subFolder = file.ParentPath[prefix.Length..];
            return !subFolder.Contains('\\') && FileProcessingUtils.IsDiscFolder(subFolder);
        }

        /// <summary>
        /// Reads the "Artist - Album" folder back out of a batch destination of the form
        /// 'lidarr/[downloadId]/[folder](/DiscN)'. Null for batches created outside of Lidarr.
        /// </summary>
        private string GetBatchAlbumFolder(string batchId)
        {
            if (string.IsNullOrEmpty(batchId) || !_batchCache.TryGetValue(batchId, out var cached))
            {
                return null;
            }

            var segments = cached?.Options?.Destination?.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments?.Length >= 3 ? segments[2] : null;
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

                // Expected once Lidarr has imported by moving the files: it removes the folder it emptied
                _logger.Debug($"Directory '{directory}' does not exist on disk, nothing to delete");
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

        /// <summary>
        /// Waits until every given transfer has reached a terminal state, with one deadline shared by
        /// all of them rather than one per file: the transitions happen concurrently on the slskd side,
        /// so the total wait is bounded by the slowest transfer instead of by the number of files.
        /// </summary>
        private void WaitForTransfersCompleted(List<(string Username, DirectoryFile File)> transfers, SlskdSettings settings)
        {
            var pending = transfers
                .Where(t => t.File.TransferState?.State != TransferStates.Completed)
                .Select(t => t.File.Id)
                .ToHashSet(StringComparer.Ordinal);

            if (pending.Count == 0)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < RemovalDrainTimeout)
            {
                // Paced here rather than through Lidarr's rate limiter, which is keyed by host and would
                // make these polls contend with the indexer's own requests to the same slskd instance
                Thread.Sleep(PollInterval);

                var queues = ExecuteGet<List<DownloadsQueue>>(BuildRequest(settings, "/api/v0/transfers/downloads/"));

                // A transfer no longer listed is as gone as a completed one
                var stillActive = queues?
                    .SelectMany(q => q.Directories)
                    .SelectMany(d => d.Files)
                    .Where(f => pending.Contains(f.Id) && f.TransferState?.State != TransferStates.Completed)
                    .Select(f => f.Id)
                    .ToHashSet(StringComparer.Ordinal);

                if (stillActive == null || stillActive.Count == 0)
                {
                    return;
                }

                pending = stillActive;
            }

            _logger.Warn($"Timed out waiting for {pending.Count} transfers to reach a terminal state, removing them anyway");
        }

        private sealed class QueueGroup
        {
            public QueueGroup(string username, OsPath outputPath, string titleBase)
            {
                Username = username;
                OutputPath = outputPath;
                TitleBase = titleBase;
            }

            public string Username { get; }
            public OsPath OutputPath { get; }
            public string TitleBase { get; }
            public List<DirectoryFile> Files { get; } = new ();

            /// <summary>
            /// How many files the group's batches enqueued in total, or null when any part of the group
            /// cannot vouch for its count (legacy transfers, or a batch fetched without its transfers),
            /// in which case the completeness check is skipped rather than guessed at.
            /// </summary>
            public int? ExpectedFiles { get; private set; } = 0;

            public void AddExpectedFiles(int? count)
            {
                ExpectedFiles = ExpectedFiles.HasValue && count.HasValue ? ExpectedFiles.Value + count.Value : null;
            }
        }

        private sealed class CachedBatch
        {
            public CachedBatch(BatchOptions options, int? expectedFiles)
            {
                Options = options;
                ExpectedFiles = expectedFiles;
            }

            public BatchOptions Options { get; }
            public int? ExpectedFiles { get; }
        }
    }
}
