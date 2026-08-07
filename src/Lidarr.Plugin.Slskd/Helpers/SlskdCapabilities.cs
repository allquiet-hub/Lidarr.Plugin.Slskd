using System;
using System.Text.RegularExpressions;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Plugin.Slskd.Helpers;

/// <summary>
/// The version of slskd the plugin requires of the instance it is talking to.
/// </summary>
public static class SlskdCapabilities
{
    /// <summary>
    /// Version that introduced transfer batches and the per-batch destination override, on which
    /// everything downstream depends: the destination is where the download id is written, and
    /// reading it back is what ties a transfer in the slskd queue to the grab recorded in Lidarr.
    /// Older instances have no equivalent, so they are refused rather than served by a second path.
    /// </summary>
    public static readonly Version MinimumVersion = new (0, 26, 0);

    private static readonly Regex VersionPattern = new (
        @"^v?(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?",
        RegexOptions.Compiled);

    /// <summary>
    /// Whether the instance is recent enough to be used at all. Development builds report a
    /// placeholder version (0.0.1.x) and are assumed to be recent.
    /// </summary>
    public static bool IsSupported(ApplicationVersion version)
    {
        if (version == null)
        {
            return false;
        }

        if (version.IsDevelopment)
        {
            return true;
        }

        var parsed = Parse(version.Current) ?? Parse(version.Full);
        return parsed != null && parsed >= MinimumVersion;
    }

    private static Version Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = VersionPattern.Match(value.Trim());
        if (!match.Success)
        {
            return null;
        }

        return new Version(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0);
    }
}
