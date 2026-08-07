using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Core.Download;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Plugin.Slskd.Helpers;

// Shared utility class for common logic
public static class FileProcessingUtils
{
    public static readonly HashSet<string> ValidAudioExtensions = new HashSet<string>
    {
        "flac", "alac", "wav", "ape", "ogg", "aac", "mp3", "wma", "m4a",
    };
    private static readonly HashSet<TransferStates> QueuedStates = new ()
    {
        TransferStates.None,
        TransferStates.Requested,
        TransferStates.Queued,
    };
    private static readonly HashSet<TransferStates> DownloadingStates = new ()
    {
        TransferStates.Initializing,
        TransferStates.InProgress,
    };
    private static readonly HashSet<TransferSubStates> FailedSubStates = new ()
    {
        TransferSubStates.Cancelled,
        TransferSubStates.TimedOut,
        TransferSubStates.Errored,
        TransferSubStates.Rejected,
        TransferSubStates.Aborted
    };
    private static readonly Regex DiscFolderPattern = new (
        @"^(CD|Disc|Disk|Side)\s*\d+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Fixed set rather than Path.GetInvalidFileNameChars(): Lidarr and slskd may run on different
    // platforms, and the segment has to be valid on the slskd side.
    private static readonly char[] InvalidSegmentChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    public static bool IsDiscFolder(string folderName) =>
        DiscFolderPattern.IsMatch(folderName ?? string.Empty);

    public static string GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var lastSep = path.LastIndexOf('\\');
        return lastSep > 0 ? path[..lastSep] : path;
    }

    private static readonly Regex DriveLetterPattern = new (@"^[a-z]:$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WordPattern = new (@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);
    private static readonly Regex LetterRunPattern = new (@"[^\p{L}\s]", RegexOptions.Compiled);

    // Matched as prefixes against the letters of a folder name, so "Music99", "musics" and
    // "MusicLibrary" are all recognised as collection folders rather than artist names.
    private static readonly string[] GenericFolderStems =
    {
        "music", "share", "complete", "download", "soulseek", "library", "collection",
        "various artists", "sorted", "unsorted", "misc", "upload", "media"
    };

    private static readonly HashSet<string> _folderToIgnore = new (StringComparer.OrdinalIgnoreCase)
    {
        "Soulseek", "Soulseek Downloads", "Soulseek Shared Folder", "FOR SOULSEEK", "soulseek to share",
        "music_spotify", "SPOTIFY", "Downloaded Music", "Torrents",
        "Musiques", "Muziek", "Music", "My Music", "MyMusic", "Muzika", "Music Box",
        "Deezer", "Deezloader", "DEEMiX", "Albums", "Album", "Recordings", "beets",
        "shared", "music-share", "unsorted", "media", "library", "new_music", "new music", "Saved Music",
        "ARCHiVED_MUSiC", "ARCHiVED MUSiC"
    };

    public static void EnsureFileExtensions<T>(List<T> files)
        where T : SlskdFile
    {
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
    }

    public static List<T> FilterValidAudioFiles<T>(this List<T> files)
        where T : SlskdFile
    {
        EnsureFileExtensions(files);
        return files.Where(file =>
            !string.IsNullOrEmpty(file.Extension) && ValidAudioExtensions.Contains(file.Extension)).ToList();
    }

    private static string DetermineCodec(IEnumerable<SlskdFile> files)
    {
        var extensions = files.Select(f => f.Extension).Distinct().ToList();
        return extensions.Count == 1 ? extensions.First().ToUpperInvariant() : null;
    }

    private static string DetermineBitRate(IEnumerable<SlskdFile> files)
    {
        var slskdFiles = files.ToList();
        var firstFile = slskdFiles.First();
        return slskdFiles.All(f => f.BitRate.HasValue && f.BitRate == firstFile.BitRate)
            ? $"{firstFile.BitRate}kbps"
            : null;
    }

    private static string DetermineSampleRateAndDepth(IEnumerable<SlskdFile> files)
    {
        var slskdFiles = files.ToList();
        var firstFile = slskdFiles.First();
        if (!slskdFiles.All(f => f.SampleRate.HasValue && f.BitDepth.HasValue))
        {
            return null;
        }

        var sampleRate = firstFile.SampleRate / 1000.0; // Convert Hz to kHz
        var bitDepth = firstFile.BitDepth;
        return $"{bitDepth}bit {sampleRate:0.0}kHz";
    }

    private static string DetermineVbr(IEnumerable<SlskdFile> files)
    {
        var slskdFiles = files.ToList();
        if (slskdFiles.All(f => f.IsVariableBitRate.HasValue && f.IsVariableBitRate.Value))
        {
            return "VBR";
        }

        if (slskdFiles.All(f => f.IsVariableBitRate.HasValue && !f.IsVariableBitRate.Value))
        {
            return "CBR";
        }

        return null;
    }

    public static string BuildTitle<T>(List<T> files, string folderOverride = null)
        where T : SlskdFile
    {
        if (files == null || !files.Any())
        {
            return string.Empty;
        }

        // A caller that already knows what the release is ("Artist - Album" from the grab) supersedes
        // everything derived from the remote path; only the quality suffix still comes from the files
        if (!string.IsNullOrWhiteSpace(folderOverride))
        {
            return string.Join(" ", new[]
            {
                folderOverride,
                DetermineCodec(files),
                DetermineBitRate(files),
                DetermineSampleRateAndDepth(files),
                DetermineVbr(files)
            }.Where(s => !string.IsNullOrEmpty(s)));
        }

        var firstFile = files.First();
        var segments = firstFile.ParentPath?.Split('\\') ?? Array.Empty<string>();

        // The first segment of a Soulseek path is the share root, never the artist: it is the user's own
        // folder name ("redtopia", "musics", "@@hnttf", "d:"). Every layout that parses correctly has the
        // artist deeper in the tree, so the root is never allowed to become part of the title.
        var shareRoot = segments.FirstOrDefault();

        // Only structural noise is dropped here; the album folder itself is always kept, even when it
        // happens to mention a format ("Inspired R3HAB - Various Artists [2015][flac]").
        var parts = segments
            .Where(s => !IsAudioExtension(s) &&
                       !IsDiscFolder(s) &&
                       !s.StartsWith("@@") &&
                       !s.StartsWith("_") &&
                       !s.StartsWith("smb-share:") &&
                       !DriveLetterPattern.IsMatch(s) &&
                       s.Length > 1)
            .ToArray();

        var fileName = firstFile.Extension != null
            ? firstFile.Name[..^(firstFile.Extension.Length + 1)]
            : firstFile.Name;

        string folderInfo;

        if (parts.Length == 0)
        {
            folderInfo = fileName;
        }
        else
        {
            var leaf = parts[^1];
            var parent = parts.Length > 1 ? parts[^2] : null;

            // The parent is prepended only when it can plausibly be the artist
            var parentIsArtist = parent != null &&
                                 !IsContainerFolder(parent) &&
                                 !IsQualityBucketFolder(parent) &&
                                 !parent.Equals(shareRoot, StringComparison.OrdinalIgnoreCase);

            if (parentIsArtist)
            {
                // Joined with a space on purpose. A dash would let Lidarr's parser split the title into
                // artist and album, and it has no way to reach the right album from a remote folder name:
                // whatever it extracts is compared against the MusicBrainz title, which the sharer never
                // saw. A title it cannot parse is the safe outcome, because Lidarr then falls back to the
                // album the search was for.
                folderInfo = parent.Contains(leaf, StringComparison.OrdinalIgnoreCase) ? parent : $"{parent} {leaf}";
            }
            else
            {
                // A bare collection folder as the only candidate says nothing about the release
                folderInfo = IsContainerFolder(leaf) ? fileName : leaf;
            }
        }

        // A lone file is not an album folder: whoever shares it names the track in the file, and the
        // folder above is usually just the artist or a collection. Without the file name the release
        // carries nothing Lidarr can identify, so a single "Artist - Song.mp3" is described by its own
        // name rather than by the folder holding it.
        if (files.Count == 1 && !folderInfo.Contains(fileName, StringComparison.OrdinalIgnoreCase))
        {
            folderInfo = fileName.Contains(folderInfo, StringComparison.OrdinalIgnoreCase)
                ? fileName
                : $"{folderInfo} {fileName}";
        }

        return string.Join(" ", new[]
        {
            folderInfo,
            DetermineCodec(files),
            DetermineBitRate(files),
            DetermineSampleRateAndDepth(files),
            DetermineVbr(files)
        }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>
    /// Recognises the collection folders users keep their library in, which say nothing about the
    /// release and only confuse Lidarr's title parsing when prepended to the album name.
    /// Matched on a stem rather than by exact name, because the variations are endless: "Music99",
    /// "musics" and "MusicLibrary" all have to collapse onto the same "music" root.
    /// </summary>
    public static bool IsContainerFolder(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (_folderToIgnore.Contains(name) || DriveLetterPattern.IsMatch(name))
        {
            return true;
        }

        // Compare on letters only, so trailing counters like "Music99" collapse onto their stem
        var letters = LetterRunPattern.Replace(name, string.Empty).Trim().ToLowerInvariant();
        letters = Regex.Replace(letters, @"\s+", " ");

        return letters.Length > 0 &&
               GenericFolderStems.Any(stem => letters.StartsWith(stem, StringComparison.Ordinal));
    }

    /// <summary>
    /// A folder naming an audio format is a quality bucket ("Redtopia FLAC 05", "MP3 320"), never an
    /// artist. Only applied to parent folders: album folders legitimately mention the format, as in
    /// "Inspired R3HAB - Various Artists [2015][flac]".
    /// </summary>
    private static bool IsQualityBucketFolder(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        WordPattern.Matches(name).Any(m => ValidAudioExtensions.Contains(m.Value.ToLowerInvariant()));

    private static bool IsAudioExtension(string s) =>
        ValidAudioExtensions.Any(ext =>
            s.Equals(ext, StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith(ext, StringComparison.OrdinalIgnoreCase));

    public static void CombineFilesWithMetadata(List<DirectoryFile> files, List<SearchResponseFile> metadataFiles)
    {
        foreach (var file in files)
        {
            var metadata = metadataFiles.FirstOrDefault(m => m.FileName == file.FileName);
            if (metadata == null)
            {
                continue;
            }

            file.BitRate = metadata.BitRate;
            file.SampleRate = metadata.SampleRate;
            file.BitDepth = metadata.BitDepth;
            file.IsVariableBitRate = metadata.IsVariableBitRate;
        }
    }

    public static (DownloadItemStatus, string) GetQueuedFilesStatus(List<DirectoryFile> files)
    {
        if (!files.Any())
        {
            return (DownloadItemStatus.Warning, null);
        }

        var states = files.Select(f => (f.TransferState.State, f.TransferState.SubState)).ToList();

        if (states.Any(s => DownloadingStates.Contains(s.State)))
        {
            return (DownloadItemStatus.Downloading, null);
        }

        if (states.Any(s => QueuedStates.Contains(s.State)))
        {
            return (DownloadItemStatus.Queued, null);
        }

        // slskd 0.26.0+ retries failed transfers with exponential backoff. Such files sit in a terminal
        // state until the next attempt, so report them as queued instead of letting Lidarr blocklist a
        // release that may still succeed.
        var retrying = files.Count(f => f.NextAttemptAt > DateTime.UtcNow);
        if (retrying > 0)
        {
            return (DownloadItemStatus.Queued, $"{retrying} files failed and are scheduled to be retried by slskd");
        }

        var allCompleted = states.All(s => s.State == TransferStates.Completed);
        if (allCompleted)
        {
            var allSucceeded = states.All(s => s.SubState == TransferSubStates.Succeeded);
            if (allSucceeded)
            {
                return (DownloadItemStatus.Completed, null);
            }

            var failedCount = states.Count(s => FailedSubStates.Contains(s.SubState));

            // Every transfer is terminal and no retry is scheduled, so this download will never
            // complete on its own. Failing it is what lets Lidarr act: a warning would leave the
            // item sitting in the queue forever, while a failure blocklists this copy of the
            // release and lets another one be grabbed.
            if (failedCount > 0)
            {
                var message = failedCount == states.Count
                    ? $"All files from user {files[0].Username} failed to download"
                    : $"{failedCount} of {states.Count} files failed and slskd will not retry them";
                return (DownloadItemStatus.Failed, message);
            }
        }

        return (DownloadItemStatus.Warning, null);
    }

    public static string Base64Encode(string plainText)
    {
        var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(plainTextBytes);
    }

    /// <summary>
    /// Makes a single path segment safe to send to slskd as part of a batch destination.
    /// slskd rejects destinations containing traversal segments before it applies its own sanitization.
    /// </summary>
    public static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsControl(character) || InvalidSegmentChars.Contains(character) ? '_' : character);
        }

        // Trailing dots are stripped by Windows and leading dots would produce '.' or '..' segments
        var sanitized = builder.ToString().Trim().Trim('.').Trim();
        return string.IsNullOrEmpty(sanitized) ? null : sanitized;
    }
}
