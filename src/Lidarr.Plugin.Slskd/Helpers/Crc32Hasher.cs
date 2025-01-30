using System;
using System.Text;

namespace NzbDrone.Plugin.Slskd.Helpers;

public static class Crc32Hasher
{
    public static uint ComputeCrc32(string input)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in Encoding.UTF8.GetBytes(input))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 & (crc & 1) * 0xFFFFFFFF);
            }
        }

        return ~crc;
    }

    public static string Crc32Base64(string input)
    {
        var crcValue = ComputeCrc32(input);
        var crcBytes = BitConverter.GetBytes(crcValue);
        return Convert.ToBase64String(crcBytes).TrimEnd('=');
    }
}
