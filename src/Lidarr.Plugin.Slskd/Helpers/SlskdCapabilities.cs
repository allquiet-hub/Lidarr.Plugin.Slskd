using System;
using System.Text.RegularExpressions;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Plugin.Slskd.Helpers;

/// <summary>
/// Feature detection for the slskd instance the plugin is talking to.
/// </summary>
public static class SlskdCapabilities
{
    /// <summary>
    /// Version that introduced transfer batches and the per-batch destination override.
    /// </summary>
    public static readonly Version BatchesMinimumVersion = new (0, 26, 0);

    private static readonly Regex VersionPattern = new (
        @"^v?(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?",
        RegexOptions.Compiled);

    /// <summary>
    /// Determines whether the instance exposes POST /api/v0/transfers/downloads/batches.
    /// Development builds report a placeholder version (0.0.1.x) and are assumed to be recent.
    /// </summary>
    public static bool SupportsBatches(ApplicationVersion version)
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
        return parsed != null && parsed >= BatchesMinimumVersion;
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
