using System;
using System.Security.Cryptography;
using System.Text;

namespace NzbDrone.Plugin.Slskd.Helpers;

public static class ReleaseIdentifier
{
    /// <summary>
    /// Derives the stable identifier of a release from the peer and folder it came from, so that a
    /// download found in the slskd queue can be tied back to the release that was grabbed.
    ///
    /// Truncated SHA-256 rather than a checksum: CRC-32 is 32 bits wide, which by the birthday bound
    /// collides with about 1 % probability across only ~300 search results, and two releases sharing
    /// an identifier means grabbing one and importing the other.
    /// </summary>
    public static string Compute(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        // 12 bytes render as 16 URL-safe base64 characters, keeping the identifier short enough to
        // live inside a download path while staying far out of collision range
        return Convert.ToBase64String(hash, 0, 12)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
