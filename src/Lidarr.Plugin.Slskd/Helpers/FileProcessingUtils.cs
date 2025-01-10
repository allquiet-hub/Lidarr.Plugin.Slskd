using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Plugin.Slskd.Helpers;

// Shared utility class for common logic
public static class FileProcessingUtils
{
    public static readonly HashSet<string> ValidAudioExtensions = new HashSet<string>
    {
        "flac", "alac", "wav", "ape", "ogg", "aac", "mp3", "wma"
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

    public static List<T> FilterValidAudioFiles<T>(List<T> files)
        where T : SlskdFile
    {
        return files.Where(file => !string.IsNullOrEmpty(file.Extension) && ValidAudioExtensions.Contains(file.Extension)).ToList();
    }

    public static string DetermineCodec(IEnumerable<SlskdFile> files)
    {
        var extensions = files.Select(f => f.Extension).Distinct().ToList();
        return extensions.Count == 1 ? extensions.First().ToUpperInvariant() : null;
    }

    public static string DetermineBitRate(IEnumerable<SlskdFile> files)
    {
        var firstFile = files.First();
        return files.All(f => f.BitRate.HasValue && f.BitRate == firstFile.BitRate)
            ? $"{firstFile.BitRate}kbps"
            : null;
    }

    public static string DetermineSampleRateAndDepth(IEnumerable<SlskdFile> files)
    {
        var firstFile = files.First();
        if (!files.All(f => f.SampleRate.HasValue && f.BitDepth.HasValue))
        {
            return null;
        }

        var sampleRate = firstFile.SampleRate / 1000.0; // Convert Hz to kHz
        var bitDepth = firstFile.BitDepth;
        return $"{bitDepth}bit {sampleRate:0.0}kHz";
    }

    public static string DetermineVbr(IEnumerable<SlskdFile> files)
    {
        if (files.All(f => f.IsVariableBitRate.HasValue && f.IsVariableBitRate.Value))
        {
            return "VBR";
        }

        if (files.All(f => f.IsVariableBitRate.HasValue && !f.IsVariableBitRate.Value))
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
        var isSingleFile = files?.Count == 1;
        var firstFile = files?.First();

        var titleBuilder = new StringBuilder();
        var fileName = isSingleFile ? firstFile.Name.Replace($".{firstFile.Extension}", "", StringComparison.InvariantCulture) : null;
        var parentFolder = (firstFile?.SecondParentFolder ?? firstFile?.FirstParentFolder)?.Replace('\\', ' ');

        titleBuilder.AppendJoin(' ', parentFolder, fileName, codec, bitRate, sampleRateAndDepth, vbrOrCbr);
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
}
