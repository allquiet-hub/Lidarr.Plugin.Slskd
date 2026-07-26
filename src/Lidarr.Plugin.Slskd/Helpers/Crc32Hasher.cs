using System;
using System.Security.Cryptography;
using System.Text;

namespace NzbDrone.Plugin.Slskd.Helpers;

public static class Crc32Hasher
{
    // Method name kept for API compatibility; implementation uses SHA-256 for collision resistance.
    // CRC-32 (32-bit) had a ~1 % birthday-paradox collision probability at ~300 search results.
    public static string Crc32Base64(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        // 12 bytes → 16 URL-safe base64 chars; collision probability < 10⁻²³ at 10 000 items
        return Convert.ToBase64String(hash, 0, 12)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
