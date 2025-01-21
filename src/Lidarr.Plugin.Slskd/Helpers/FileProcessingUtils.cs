using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Plugin.Slskd.Helpers;

// Shared utility class for common logic
public static class FileProcessingUtils
{
    private static readonly HashSet<string> ValidAudioExtensions = new HashSet<string>
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
        return files.Where(file => !string.IsNullOrEmpty(file.Extension) && ValidAudioExtensions.Contains(file.Extension)).ToList();
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
        var codec = DetermineCodec(files);
        var bitRate = DetermineBitRate(files);
        var sampleRateAndDepth = DetermineSampleRateAndDepth(files);
        var vbrOrCbr = DetermineVbr(files);
        var firstFile = files?.First();

        var titleBuilder = new StringBuilder();
        var folderToIgnore = new HashSet<string>()
        {
            "Soulseek", "Soulseek Downloads", "Soulseek Shared Folder", "FOR SOULSEEK", "soulseek to share",
            "music_spotify", "SPOTIFY", "Downloaded Music", "Torrents",
            "Musiques", "Muziek", "Music", "My Music", "MyMusic", "Muzika", "Music Box",
            "Deezer", "Deezloader", "DEEMiX", "Albums", "Album", "Recordings", "beets",
            "shared", "music-share", "unsorted", "media", "library", "new_music", "new music", "Saved Music",
            "ARCHiVED_MUSiC", "ARCHiVED MUSiC"
        };
        var parts = firstFile?.ParentPath.Split('\\').Where(
                s => !folderToIgnore.ContainsIgnoreCase(s) &&
                     !ValidAudioExtensions.ContainsIgnoreCase(s) &&
                     !ValidAudioExtensions.Any(ext => s.StartsWith(ext, StringComparison.InvariantCulture)) &&
                     !s.StartsWith("@@", StringComparison.InvariantCulture) &&
                     !s.StartsWith("_", StringComparison.InvariantCulture) &&
                     !s.StartsWith("smb-share:", StringComparison.InvariantCulture) &&
                     s.Length != 1)
            .ToArray();

        var fileName = firstFile?.Name.Replace($".{firstFile.Extension}", "", StringComparison.InvariantCulture);

        // Single Parent folder
        var firstParentFolder = parts?.Length > 0
            ? parts[^1] // Single parent folder
            : null;

        // Parent folder: Two levels above the file
        var secondParentFolder = parts?.Length > 1 && !parts[^1].Contains(parts[^2]) // Ensure last does not fully contain second-to-last
            ? string.Join(" ", parts[^2..]) // Two directories above the file
            : null;

        titleBuilder.AppendJoin(' ', secondParentFolder ?? firstParentFolder ?? fileName, codec, bitRate, sampleRateAndDepth, vbrOrCbr);
        return titleBuilder.ToString().Trim();
    }

    private static void EnsureFileExtensions(IEnumerable<SlskdFile> files)
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
        if (files.Any(f => DownloadingStates.Contains(f.TransferState.State)))
        {
            return (DownloadItemStatus.Downloading, null);
        }

        if (files.Any(f => QueuedStates.Contains(f.TransferState.State)))
        {
            return (DownloadItemStatus.Queued, null);
        }

        if (files.All(f => f.TransferState.State == TransferStates.Completed && f.TransferState.SubState == TransferSubStates.Succeeded))
        {
            return (DownloadItemStatus.Completed, null);
        }

        if (files.All(f => f.TransferState.State == TransferStates.Completed
                           && FailedSubStates.Contains(f.TransferState.SubState)))
        {
            return (DownloadItemStatus.Failed, $"All files in directory {files.First().ParentPath} from user {files.First().Username} have failed");
        }

        if (files.Any(f => f.TransferState.State == TransferStates.Completed && f.TransferState.SubState == TransferSubStates.Succeeded) &&
            files.Any(f => FailedSubStates.Contains(f.TransferState.SubState)))
        {
            var completedFiles = files.Where(f => f.TransferState.State == TransferStates.Completed && f.TransferState.SubState == TransferSubStates.Succeeded);
            var failedFiles = files.Where(f => FailedSubStates.Contains(f.TransferState.SubState));

            return (DownloadItemStatus.Warning, $"{completedFiles.Count()} files downloaded, {failedFiles.Count()} failed, consider retrying the download from the slskd client");
        }

        return (DownloadItemStatus.Warning, null);
    }

    public static string Base64Encode(string plainText)
    {
        var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(plainTextBytes);
    }
}
