using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Plugin.Slskd.Helpers;

/// <summary>
/// An slskd setting whose current value breaks or degrades the integration, together with where it
/// lives in the YAML configuration and the value that repairs it.
/// </summary>
public class SlskdConfigIssue
{
    public SlskdConfigIssue(string shortName, string description, string[] yamlPath, string desiredValue)
    {
        ShortName = shortName;
        Description = description;
        YamlPath = yamlPath;
        DesiredValue = desiredValue;
    }

    public string ShortName { get; }
    public string Description { get; }
    public string[] YamlPath { get; }
    public string DesiredValue { get; }
}

/// <summary>
/// The single authority on which slskd settings count as broken, shared by the health check that
/// warns about them and the Test repair that rewrites them, so the two can never disagree.
/// </summary>
public static class SlskdConfigIssues
{
    public static List<SlskdConfigIssue> Find(SlskdOptions options)
    {
        var issues = new List<SlskdConfigIssue>();

        if (options == null)
        {
            return issues;
        }

        var mode = options.Transfers?.Download?.Destination?.Permissions?.Mode;
        if (HasNoExecuteBit(mode))
        {
            issues.Add(new SlskdConfigIssue(
                "permissions.mode",
                $"slskd applies permission mode {mode} to completed download folders as well as files, " +
                "which leaves them untraversable; set 'transfers.download.destination.permissions.mode' " +
                "to 777 (no effect if slskd runs on Windows)",
                new[] { "transfers", "download", "destination", "permissions", "mode" },
                "777"));
        }

        if (!options.RemoteFileManagement)
        {
            issues.Add(new SlskdConfigIssue(
                "remote_file_management",
                "slskd has 'remote_file_management' disabled, so Lidarr cannot delete completed " +
                "downloads after importing them and they will accumulate",
                new[] { "remote_file_management" },
                "true"));
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
