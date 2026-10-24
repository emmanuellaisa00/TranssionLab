using System.Buffers.Binary;
using System.Text;
using TranssionLab.Core.Models;

namespace TranssionLab.Firmware.Parsers;

public static class AndroidSparseAndBootParsers
{
    public const uint SparseMagic = 0xED26FF3A;
    public const ushort ChunkTypeRaw = 0xCAC1;
    public const ushort ChunkTypeFill = 0xCAC2;
    public const ushort ChunkTypeDontCare = 0xCAC3;
    public const ushort ChunkTypeCrc32 = 0xCAC4;

    public static SparseImageHeaderInfo? TryParseSparseImage(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 28)
            return null;

        stream.Position = 0;
        Span<byte> hdr = stackalloc byte[28];
        if (stream.Read(hdr) != 28)
            return null;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr[..4]);
        if (magic != SparseMagic)
            return null;

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(4, 2));
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(6, 2));
        ushort fileHdrSz = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(8, 2));
        ushort chunkHdrSz = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(10, 2));
        uint blkSz = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(12, 4));
        uint totalBlks = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(16, 4));
        uint totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(20, 4));
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(24, 4));

        if (fileHdrSz < 28 || chunkHdrSz < 12 || blkSz == 0)
            return null;

        stream.Position = fileHdrSz;
        Span<byte> chdr = stackalloc byte[12];
        int rawCount = 0, fillCount = 0, dontCareCount = 0, crcCount = 0;
        long accountedBlocks = 0;
        bool valid = true;

        for (uint i = 0; i < totalChunks && i < 200_000; i++)
        {
            if (stream.Position + chunkHdrSz > stream.Length)
            {
                valid = false;
                break;
            }

            long chunkStart = stream.Position;
            if (stream.Read(chdr) != 12)
            {
                valid = false;
                break;
            }

            ushort chunkType = BinaryPrimitives.ReadUInt16LittleEndian(chdr[..2]);
            uint chunkBlocks = BinaryPrimitives.ReadUInt32LittleEndian(chdr.Slice(4, 4));
            uint totalSz = BinaryPrimitives.ReadUInt32LittleEndian(chdr.Slice(8, 4));

            if (totalSz < chunkHdrSz || chunkStart + totalSz > stream.Length)
            {
                valid = false;
                break;
            }

            switch (chunkType)
            {
                case ChunkTypeRaw:
                    rawCount++;
                    accountedBlocks += chunkBlocks;
                    break;
                case ChunkTypeFill:
                    fillCount++;
                    accountedBlocks += chunkBlocks;
                    break;
                case ChunkTypeDontCare:
                    dontCareCount++;
                    accountedBlocks += chunkBlocks;
                    break;
                case ChunkTypeCrc32:
                    crcCount++;
                    break;
                default:
                    valid = false;
                    break;
            }

            stream.Position = chunkStart + totalSz;
        }

        if (accountedBlocks != totalBlks)
        {
            valid = false;
        }

        return new SparseImageHeaderInfo(
            MajorVersion: major,
            MinorVersion: minor,
            BlockSizeBytes: blkSz,
            TotalBlocks: totalBlks,
            TotalChunks: totalChunks,
            ImageChecksum: checksum,
            TotalUnsparsedBytes: (long)totalBlks * blkSz,
            RawChunkCount: rawCount,
            FillChunkCount: fillCount,
            DontCareChunkCount: dontCareCount,
            CrcChunkCount: crcCount,
            AccountingValid: valid);
    }

    public static BootImageHeaderInfo? TryParseBootOrVendorBoot(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 1632)
            return null;

        stream.Position = 0;
        Span<byte> buf = stackalloc byte[2112];
        int read = stream.Read(buf);
        if (read < 1632)
            return null;

        var magic = Encoding.ASCII.GetString(buf[..8]);
        if (magic == "ANDROID!")
        {
            // Inspect header_version: at offset 40 in v0/v1/v2 AND ALSO at offset 40 in v3/v4!
            uint headerVersion = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(40, 4));
            if (headerVersion >= 3 && headerVersion <= 4)
            {
                // Android 11–16 boot_img_hdr_v3 / v4 layout
                uint kernelSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(8, 4));
                uint ramdiskSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(12, 4));
                uint osVerRaw = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(16, 4));
                uint hdrSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(20, 4));
                var cmdline = ReadNullTerminatedAscii(buf.Slice(44, Math.Min(1536, read - 44)));
                var (osVer, patch) = DecodeOsVersionAndPatch(osVerRaw);
                return new BootImageHeaderInfo(
                    magic, headerVersion, kernelSize, ramdiskSize, hdrSize, 4096, osVer, patch, cmdline);
            }
            else
            {
                // Legacy v0 / v1 / v2 layout
                uint kernelSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(8, 4));
                uint ramdiskSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(16, 4));
                uint secondSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(24, 4));
                uint pageSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(36, 4));
                uint osVerRaw = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(44, 4));
                var cmdline = ReadNullTerminatedAscii(buf.Slice(64, 512));
                var (osVer, patch) = DecodeOsVersionAndPatch(osVerRaw);
                return new BootImageHeaderInfo(
                    magic, headerVersion, kernelSize, ramdiskSize, secondSize, pageSize, osVer, patch, cmdline);
            }
        }

        if (magic == "VNDRBOOT")
        {
            // vendor_boot_img_hdr_v3 / v4
            uint headerVersion = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(8, 4));
            uint pageSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(12, 4));
            uint vendorRamdiskSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(24, 4));
            var cmdline = ReadNullTerminatedAscii(buf.Slice(28, Math.Min(2048, read - 28)));
            uint dtbSize = read >= 2104 ? BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(2100, 4)) : 0;
            return new BootImageHeaderInfo(
                magic, headerVersion, 0, vendorRamdiskSize, dtbSize, pageSize, "GKI Vendor Boot", "N/A", cmdline);
        }

        return null;
    }

    public static (string OsVersion, string PatchLevel) DecodeOsVersionAndPatch(uint raw)
    {
        if (raw == 0)
            return ("Unspecified", "Unspecified");

        uint verPart = raw >> 11;
        uint patchPart = raw & 0x7FF;

        uint a = (verPart >> 14) & 0x7F;
        uint b = (verPart >> 7) & 0x7F;
        uint c = verPart & 0x7F;

        uint year = 2000 + (patchPart >> 4);
        uint month = patchPart & 0x0F;

        var osStr = $"{a}.{b}.{c}";
        var patchStr = month is >= 1 and <= 12 ? $"{year:D4}-{month:D2}" : "Unspecified";
        return (osStr, patchStr);
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        var slice = nul >= 0 ? bytes[..nul] : bytes;
        return Encoding.ASCII.GetString(slice).Trim();
    }
}
