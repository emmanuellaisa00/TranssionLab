using System.Buffers.Binary;
using System.Text;
using TranssionLab.Core.Models;

namespace TranssionLab.Firmware.Parsers;

/// <summary>
/// Dependency-free parser for Android A/B & Virtual A/B OTA payload.bin (CrAU v2)
/// including lightweight Protobuf wire-format decoding of DeltaArchiveManifest.
/// </summary>
public static class AndroidPayloadBinParser
{
    public static PayloadManifestInfo? TryParsePayloadHeaderAndManifest(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 24)
            return null;

        stream.Position = 0;
        Span<byte> hdr = stackalloc byte[24];
        if (stream.Read(hdr) != 24)
            return null;

        if (Encoding.ASCII.GetString(hdr[..4]) != "CrAU")
            return null;

        ulong version = BinaryPrimitives.ReadUInt64BigEndian(hdr.Slice(4, 8));
        ulong manifestLen = BinaryPrimitives.ReadUInt64BigEndian(hdr.Slice(12, 8));
        uint sigLen = version >= 2 ? BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(20, 4)) : 0;

        long headerSize = version >= 2 ? 24 : 20;
        if (manifestLen == 0 || manifestLen > 32 * 1024 * 1024 || headerSize + (long)manifestLen > stream.Length)
            return null;

        stream.Position = headerSize;
        var manifestBytes = new byte[(int)manifestLen];
        int read = stream.Read(manifestBytes, 0, manifestBytes.Length);
        if (read != manifestBytes.Length)
            return null;

        var (blockSize, minorVer, partitions) = DecodeDeltaArchiveManifest(manifestBytes);
        return new PayloadManifestInfo(version, manifestLen, sigLen, blockSize, minorVer, partitions);
    }

    private static (uint BlockSize, uint MinorVersion, List<PayloadPartitionSummary> Partitions) DecodeDeltaArchiveManifest(ReadOnlySpan<byte> data)
    {
        uint blockSize = 4096;
        uint minorVersion = 0;
        var partitions = new List<PayloadPartitionSummary>();

        int offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadVarint(data, ref offset, out ulong tag))
                break;

            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x07);

            switch (wireType)
            {
                case 0: // Varint
                    if (!TryReadVarint(data, ref offset, out ulong v))
                        return (blockSize, minorVersion, partitions);
                    if (fieldNumber == 3) blockSize = (uint)v;
                    else if (fieldNumber == 12) minorVersion = (uint)v;
                    break;

                case 2: // Length-delimited
                    if (!TryReadVarint(data, ref offset, out ulong len) || offset + (int)len > data.Length)
                        return (blockSize, minorVersion, partitions);
                    var slice = data.Slice(offset, (int)len);
                    offset += (int)len;

                    if (fieldNumber == 13) // repeated PartitionUpdate partitions = 13
                    {
                        var part = DecodePartitionUpdate(slice);
                        if (!string.IsNullOrWhiteSpace(part.PartitionName))
                        {
                            partitions.Add(part);
                        }
                    }
                    break;

                case 1: // 64-bit
                    offset += 8;
                    break;
                case 5: // 32-bit
                    offset += 4;
                    break;
                default:
                    return (blockSize, minorVersion, partitions);
            }
        }

        return (blockSize, minorVersion, partitions);
    }

    private static PayloadPartitionSummary DecodePartitionUpdate(ReadOnlySpan<byte> data)
    {
        string name = string.Empty;
        ulong sizeBytes = 0;
        string? shaHex = null;
        int opCount = 0;

        int offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadVarint(data, ref offset, out ulong tag))
                break;

            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x07);

            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(data, ref offset, out _))
                        return new PayloadPartitionSummary(name, sizeBytes, shaHex, opCount);
                    break;

                case 2:
                    if (!TryReadVarint(data, ref offset, out ulong len) || offset + (int)len > data.Length)
                        return new PayloadPartitionSummary(name, sizeBytes, shaHex, opCount);
                    var sub = data.Slice(offset, (int)len);
                    offset += (int)len;

                    if (fieldNumber == 1) // partition_name
                    {
                        name = Encoding.UTF8.GetString(sub);
                    }
                    else if (fieldNumber == 7) // PartitionInfo new_partition_info = 7
                    {
                        (sizeBytes, shaHex) = DecodePartitionInfo(sub);
                    }
                    else if (fieldNumber == 8) // repeated InstallOperation operations = 8
                    {
                        opCount++;
                    }
                    break;

                case 1:
                    offset += 8;
                    break;
                case 5:
                    offset += 4;
                    break;
                default:
                    return new PayloadPartitionSummary(name, sizeBytes, shaHex, opCount);
            }
        }

        return new PayloadPartitionSummary(name, sizeBytes, shaHex, opCount);
    }

    private static (ulong Size, string? HashHex) DecodePartitionInfo(ReadOnlySpan<byte> data)
    {
        ulong size = 0;
        string? hash = null;
        int offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadVarint(data, ref offset, out ulong tag))
                break;
            int field = (int)(tag >> 3);
            int wt = (int)(tag & 0x07);
            if (wt == 0)
            {
                if (!TryReadVarint(data, ref offset, out ulong v)) break;
                if (field == 1) size = v;
            }
            else if (wt == 2)
            {
                if (!TryReadVarint(data, ref offset, out ulong len) || offset + (int)len > data.Length) break;
                var sub = data.Slice(offset, (int)len);
                offset += (int)len;
                if (field == 2) hash = Convert.ToHexString(sub).ToLowerInvariant();
            }
            else if (wt == 1) offset += 8;
            else if (wt == 5) offset += 4;
            else break;
        }
        return (size, hash);
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong result)
    {
        result = 0;
        int shift = 0;
        while (offset < data.Length && shift < 64)
        {
            byte b = data[offset++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return true;
            shift += 7;
        }
        return false;
    }
}
