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

    public static string BuildTitle<T>(List<T> files)
        where T : SlskdFile
    {
        if (files == null || !files.Any())
        {
            return string.Empty;
        }

        var firstFile = files.First();
        var parts = firstFile.ParentPath.Split('\\')
            .Where(s => !_folderToIgnore.Contains(s) &&
                       !IsAudioExtension(s) &&
                       !IsDiscFolder(s) &&
                       !s.StartsWith("@@") &&
                       !s.StartsWith("_") &&
                       !s.StartsWith("smb-share:") &&
                       s.Length > 1)
            .ToArray();

        var fileName = firstFile.Extension != null
            ? firstFile.Name[..^(firstFile.Extension.Length + 1)]
            : firstFile.Name;

        var folderInfo = parts.Length switch
        {
            0 => fileName,
            1 => parts[0],
            _ => parts[^2].Contains(parts[^1]) ? parts[^2] : string.Join(" ", parts[^2..])
        };

        return string.Join(" ", new[]
        {
            folderInfo,
            DetermineCodec(files),
            DetermineBitRate(files),
            DetermineSampleRateAndDepth(files),
            DetermineVbr(files)
        }.Where(s => !string.IsNullOrEmpty(s)));
    }

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

            var allFailed = states.All(s => FailedSubStates.Contains(s.SubState));
            if (allFailed)
            {
                return (DownloadItemStatus.Failed, $"All files in directory {files[0].ParentPath} from user {files[0].Username} have failed");
            }

            var succeededCount = states.Count(s => s.SubState == TransferSubStates.Succeeded);
            var failedCount = states.Count(s => FailedSubStates.Contains(s.SubState));

            if (succeededCount > 0 && failedCount > 0)
            {
                return (DownloadItemStatus.Warning, $"{succeededCount} files downloaded, {failedCount} failed, consider retrying the download from the slskd client");
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
