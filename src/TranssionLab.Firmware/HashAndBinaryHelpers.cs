using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using TranssionLab.Core.Enums;

namespace TranssionLab.Firmware;

public static class HashAndBinaryHelpers
{
    private static readonly HashSet<string> ProtectedNvAndSecurityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvram", "nvdata", "nvcfg", "persist", "proinfo", "protect1", "protect2",
        "seccfg", "otp", "preloader", "prodnv", "miscdata", "phasecheck",
        "l_fixnv1", "l_fixnv2", "l_runtimenv1", "fsg", "modemst1", "modemst2", "frp"
    };

    private static readonly HashSet<string> BootChainCriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "preloader", "splloader", "uboot", "lk", "lk2", "bootloader",
        "tee", "tee1", "tee2", "gz", "gz1", "gz2", "spmfw", "sspm",
        "mcupm", "dpm", "pi_img", "scp", "scp1", "scp2", "sbl1", "xbl", "abl"
    };

    public static string StripSlotSuffix(string partitionName)
    {
        if (string.IsNullOrWhiteSpace(partitionName))
            return string.Empty;
        var trimmed = partitionName.Trim();
        if (trimmed.EndsWith("_a", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("_b", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[..^2];
        }
        return trimmed;
    }

    public static bool IsAntiTheftFrpPartition(string partitionName)
    {
        var baseName = StripSlotSuffix(partitionName);
        return string.Equals(baseName, "frp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(baseName, "persistent", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(baseName, "config", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBootChainCriticalPartition(string partitionName)
    {
        var baseName = StripSlotSuffix(partitionName);
        return BootChainCriticalNames.Contains(baseName);
    }

    public static bool IsProtectedPartition(string partitionName)
    {
        if (string.IsNullOrWhiteSpace(partitionName))
            return false;
        var baseName = StripSlotSuffix(partitionName);
        return ProtectedNvAndSecurityNames.Contains(baseName);
    }

    public static string SanitizeFileNameComponent(string? raw, string fallback = "device")
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 || ch is ':' or '\\' or '/' or ' ' ? '_' : ch);
        }

        var result = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    public static void EnsureParentDirectoryExists(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        var fullPath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public static string FormatBytesHuman(long? bytes)
    {
        if (!bytes.HasValue || bytes.Value < 0)
            return "Unknown";
        double b = bytes.Value;
        if (b >= 1024d * 1024 * 1024)
            return $"{b / (1024d * 1024 * 1024):F2} GiB";
        if (b >= 1024d * 1024)
            return $"{b / (1024d * 1024):F2} MiB";
        if (b >= 1024d)
            return $"{b / 1024d:F1} KiB";
        return $"{bytes.Value} B";
    }

    public static async Task<(string Sha256Hex, string Crc32Hex)> ComputeHashesAsync(
        string filePath,
        long maxBytesToHash = 512L * 1024 * 1024,
        CancellationToken ct = default)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        if (fs.Length > maxBytesToHash)
        {
            return ("SKIPPED_LARGE_IMAGE_OVER_512MB", "SKIPPED");
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var crc = new Crc32();
        var buffer = new byte[65536];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            crc.Append(buffer.AsSpan(0, read));
        }

        var shaHex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        var crcBytes = crc.GetCurrentHash();
        uint crcVal = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes);
        return (shaHex, $"{crcVal:x8}");
    }

    public static (FirmwareFormatKind Kind, string Summary) SniffStreamMagic(Stream stream, string fileNameHint)
    {
        var startPos = stream.CanSeek ? stream.Position : 0;
        Span<byte> header = stackalloc byte[8192];
        int read = stream.Read(header);
        if (stream.CanSeek)
            stream.Position = startPos;

        if (read >= 4)
        {
            uint le32 = BinaryPrimitives.ReadUInt32LittleEndian(header);
            uint be32 = BinaryPrimitives.ReadUInt32BigEndian(header);

            if (le32 == 0xED26FF3A)
                return (FirmwareFormatKind.AndroidSparseImage, "Android Sparse Image (0xED26FF3A)");

            if (be32 == 0x43724155) // "CrAU"
                return (FirmwareFormatKind.AndroidPayloadBin, "Android OTA CrAU Payload");

            if (le32 == 0x616C4467) // LP_METADATA_GEOMETRY_MAGIC at offset 0
                return (FirmwareFormatKind.AndroidSuperImage, "Android Dynamic Super Image (LpMetadata)");

            if (read >= 0x1004 && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x1000, 4)) == 0x616C4467)
                return (FirmwareFormatKind.AndroidSuperImage, "Android Dynamic Super Image (LpMetadata @ 0x1000)");

            if (header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04)
                return (FirmwareFormatKind.ZipArchive, "ZIP Container");

            if (header[0] == 0x1F && header[1] == 0x8B)
                return (FirmwareFormatKind.TarGzipArchive, "GZip Compressed Archive");

            if (read >= 8)
            {
                var magic8 = Encoding.ASCII.GetString(header[..8]);
                if (magic8 == "ANDROID!")
                    return (FirmwareFormatKind.AndroidBootImage, "Android Boot/Recovery Image (ANDROID!)");
                if (magic8 == "VNDRBOOT")
                    return (FirmwareFormatKind.AndroidVendorBootImage, "Android Vendor Boot Image (VNDRBOOT)");
            }

            if (Encoding.ASCII.GetString(header[..4]) == "AVB0")
                return (FirmwareFormatKind.AndroidVbmetaImage, "Android Verified Boot Metadata (AVB0)");
        }

        if (read >= 265 && Encoding.ASCII.GetString(header.Slice(257, 5)) == "ustar")
            return (FirmwareFormatKind.TarArchive, "POSIX USTAR Archive");

        if (read >= 64)
        {
            var utf16Prefix = Encoding.Unicode.GetString(header[..16]).TrimEnd('\0');
            if (utf16Prefix.StartsWith("BP_R", StringComparison.OrdinalIgnoreCase))
                return (FirmwareFormatKind.UnisocPacContainer, $"Unisoc PAC Container ({utf16Prefix})");
        }

        if (read >= 0x43A)
        {
            ushort ext4Magic = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(0x438, 2));
            if (ext4Magic == 0xEF53)
                return (FirmwareFormatKind.RawPartitionImage, "Raw ext4 Filesystem Image");
        }
        if (read >= 0x404)
        {
            uint erofsMagic = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x400, 4));
            if (erofsMagic == 0xE0F5E1E2)
                return (FirmwareFormatKind.RawPartitionImage, "Raw EROFS Read-Only Filesystem Image");
            if (erofsMagic == 0xF2F52010)
                return (FirmwareFormatKind.RawPartitionImage, "Raw F2FS Flash-Friendly Filesystem Image");
        }

        if (read >= 16)
        {
            var textHead = Encoding.UTF8.GetString(header[..Math.Min(read, 4096)]);
            if (textHead.Contains("partition_index:", StringComparison.OrdinalIgnoreCase) &&
                (textHead.Contains("physical_start_addr:", StringComparison.OrdinalIgnoreCase) ||
                 textHead.Contains("partition_size:", StringComparison.OrdinalIgnoreCase) ||
                 textHead.Contains("MTK_PLATFORM_CFG", StringComparison.OrdinalIgnoreCase)))
            {
                return (FirmwareFormatKind.MediaTekScatterText, "MediaTek Scatter Descriptor (YAML/Text)");
            }
            if (textHead.Contains("<scatter", StringComparison.OrdinalIgnoreCase) ||
                (textHead.Contains("<?xml", StringComparison.OrdinalIgnoreCase) && textHead.Contains("storage_type", StringComparison.OrdinalIgnoreCase)))
            {
                return (FirmwareFormatKind.MediaTekScatterXml, "MediaTek Scatter Descriptor (XML)");
            }
        }

        var ext = Path.GetExtension(fileNameHint).ToLowerInvariant();
        if (ext is ".img" or ".bin")
            return (FirmwareFormatKind.RawPartitionImage, "Raw Binary Partition Image");

        return (FirmwareFormatKind.Unsupported, "Unrecognized or unsupported binary format");
    }
}
