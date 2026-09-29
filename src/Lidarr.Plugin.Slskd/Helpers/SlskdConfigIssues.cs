using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Plugin.Slskd.Helpers;

/// <summary>
/// The single authority on which slskd settings count as broken, shared by the health check and the
/// download client's Test so that the two can never disagree.
/// </summary>
public static class SlskdConfigIssues
{
    public static List<string> Find(SlskdOptions options)
    {
        var issues = new List<string>();

        if (options == null)
        {
            return issues;
        }

        var mode = options.Transfers?.Download?.Destination?.Permissions?.Mode;
        if (HasNoExecuteBit(mode))
        {
            issues.Add($"slskd applies permission mode {mode} to completed download folders as well as files, " +
                "which leaves them untraversable; set 'transfers.download.destination.permissions.mode' " +
                "to 777 (no effect if slskd runs on Windows)");
        }

        if (!options.RemoteFileManagement)
        {
            issues.Add("slskd has 'remote_file_management' disabled, so Lidarr cannot delete completed " +
                "downloads after importing them and they will accumulate");
        }

        return issues;
    }

    /// <summary>
    /// True when no permission class at all retains the execute bit, which is the unambiguous case:
    /// the directories cannot be entered by anyone but root, whatever user Lidarr runs as. Modes
    /// that only restrict some classes (750, 744) are somebody's deliberate choice and left alone.
    /// </summary>
    private static bool HasNoExecuteBit(string mode)
    {
        if (mode.IsNullOrWhiteSpace())
        {
            return false;
        }

        var digits = mode.Trim();

        // Only the three least significant digits describe execute permissions
        if (digits.Length > 3)
        {
            digits = digits[^3..];
        }

        return digits.All(d => char.IsDigit(d) && (d - '0') % 2 == 0);
    }
}
