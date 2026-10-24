using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using TranssionLab.Core.Enums;

namespace TranssionLab.Firmware;

public static class HashAndBinaryHelpers
{
    private static readonly HashSet<string> ProtectedPartitionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvram", "nvdata", "nvcfg", "persist", "proinfo", "protect1", "protect2",
        "seccfg", "otp", "preloader", "prodnv", "miscdata", "phasecheck",
        "l_fixnv1", "l_fixnv2", "l_runtimenv1", "fsg", "modemst1", "modemst2"
    };

    public static bool IsProtectedPartition(string partitionName)
    {
        if (string.IsNullOrWhiteSpace(partitionName))
            return false;
        var baseName = partitionName.Trim();
        if (baseName.EndsWith("_a", StringComparison.OrdinalIgnoreCase) ||
            baseName.EndsWith("_b", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^2];
        }
        return ProtectedPartitionNames.Contains(baseName);
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
        Span<byte> header = stackalloc byte[4096];
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

        // Check Unisoc PAC header UTF-16LE version ("BP_R")
        if (read >= 64)
        {
            var utf16Prefix = Encoding.Unicode.GetString(header[..16]).TrimEnd('\0');
            if (utf16Prefix.StartsWith("BP_R", StringComparison.OrdinalIgnoreCase))
                return (FirmwareFormatKind.UnisocPacContainer, $"Unisoc PAC Container ({utf16Prefix})");
        }

        // Check inner raw filesystem magics
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

        // Check text-based scatter files
        if (read >= 16)
        {
            var textHead = Encoding.UTF8.GetString(header[..Math.Min(read, 1024)]);
            if (textHead.Contains("partition_index:", StringComparison.OrdinalIgnoreCase) &&
                textHead.Contains("physical_start_addr:", StringComparison.OrdinalIgnoreCase))
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
