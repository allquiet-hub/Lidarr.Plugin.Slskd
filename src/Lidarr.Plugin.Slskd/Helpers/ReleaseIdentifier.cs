using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NzbDrone.Plugin.Slskd.Helpers;

/// <summary>
/// The two identities the plugin derives, kept apart because they answer different questions.
///
/// A release identifier names a folder a peer is offering, and nothing more. Lidarr collapses the
/// same folder found by several query tiers onto it, and the blocklist records a refusal against it,
/// so it must not vary with the album the folder was offered for.
///
/// A download identifier names one grab: this folder, for this album. The same folder is routinely
/// offered for several albums at once, since a search for a single matches the track sitting inside
/// the album's folder. Two such grabs sharing one identifier reach Lidarr as a single tracked
/// download claiming both albums, which the import can satisfy for at most one of them and fails for
/// every one of them.
/// </summary>
public static class ReleaseIdentifier
{
    /// <summary>
    /// Joins the parts of a composite key. NUL appears in neither a Soulseek username nor a shared
    /// path, so no two different splits of the parts can produce the same string to hash.
    /// </summary>
    private const string Separator = "\0";

    public static string ForRelease(string username, string folder) =>
        Compute(username, folder);

    /// <summary>
    /// Appends the album to the release identifier rather than hashing the pair. Hashing would be
    /// just as unique but would discard the indexer id Lidarr prefixes onto every release guid, and
    /// that prefix is what makes a folder sitting in slskd legible: it names which side created it.
    /// Both parts stay readable, so a download directory can be traced back to its grab by eye.
    ///
    /// Derived rather than drawn at random so that re-grabbing the same folder for the same album
    /// settles on the batch and destination already in flight instead of starting a second copy.
    /// </summary>
    public static string ForDownload(string releaseIdentifier, int albumId) =>
        $"{releaseIdentifier}_{albumId.ToString(CultureInfo.InvariantCulture)}";

    private static string Compute(params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(Separator, parts)));

        // Truncated SHA-256 rather than a checksum: CRC-32 is 32 bits wide, which by the birthday
        // bound collides with about 1 % probability across only ~300 search results, and two releases
        // sharing an identifier means grabbing one and importing the other. 12 bytes render as 16
        // base64 characters, short enough to live inside a download path while staying far out of
        // collision range, and the alphabet is made URL-safe because the identifier becomes a path
        // segment that is later read back out of one.
        return Convert.ToBase64String(hash, 0, 12)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
