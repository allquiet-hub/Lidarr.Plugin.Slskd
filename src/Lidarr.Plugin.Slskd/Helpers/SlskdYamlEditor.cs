using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Slskd.Helpers;

/// <summary>
/// Rewrites a single value inside slskd's YAML configuration by editing just that line, so the rest
/// of the file — ordering, comments, commented-out examples — survives byte for byte. A YAML library
/// would round-trip the document and lose all of that, and the file being edited is one the user
/// maintains by hand. The result is never trusted blindly: the caller validates it through slskd's
/// own validation endpoint before saving.
/// </summary>
public static class SlskdYamlEditor
{
    private static readonly Regex KeyLine = new (@"^(?<indent>[ ]*)(?<key>[A-Za-z_][A-Za-z0-9_\-]*):(?<rest>.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Sets 'key: value' at the given path. Tracks the mapping structure by indentation, which is the
    /// only structure slskd's configuration uses at the paths being edited. Returns the possibly
    /// edited text, whether it changed, and whether the path was found at all; a top-level key that
    /// does not exist is appended to the end of the file, a nested one is left for the caller to
    /// report rather than guessed at.
    /// </summary>
    public static (string Yaml, bool Changed, bool Found) SetValue(string yaml, string[] path, string value)
    {
        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var stack = new System.Collections.Generic.List<(int Indent, string Key)>();

        for (var i = 0; i < lines.Length; i++)
        {
            var match = KeyLine.Match(lines[i]);
            if (!match.Success || lines[i].TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indent = match.Groups["indent"].Value.Length;

            while (stack.Count > 0 && stack[^1].Indent >= indent)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            stack.Add((indent, match.Groups["key"].Value));

            if (stack.Count != path.Length || !stack.Select(s => s.Key).SequenceEqual(path, StringComparer.Ordinal))
            {
                continue;
            }

            // Split the remainder into the value and a trailing comment, keeping the comment in place
            var rest = match.Groups["rest"].Value;
            var commentIndex = rest.IndexOf('#');
            var currentValue = (commentIndex >= 0 ? rest[..commentIndex] : rest).Trim();
            var comment = commentIndex >= 0 ? " " + rest[commentIndex..].TrimEnd() : string.Empty;

            if (currentValue == value)
            {
                return (yaml, false, true);
            }

            lines[i] = $"{match.Groups["indent"].Value}{match.Groups["key"].Value}: {value}{comment}";
            return (string.Join(newline, lines), true, true);
        }

        if (path.Length == 1)
        {
            var appended = yaml.TrimEnd('\r', '\n') + $"{newline}{path[0]}: {value}{newline}";
            return (appended, true, true);
        }

        return (yaml, false, false);
    }
}
