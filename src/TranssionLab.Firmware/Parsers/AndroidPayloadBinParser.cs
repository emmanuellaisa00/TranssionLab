using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Models;
using ZstdSharp;

namespace TranssionLab.Firmware.Parsers;

/// <summary>
/// Full parser and extractor for Android A/B & Virtual A/B OTA payload.bin (CrAU v2).
/// Supports DeltaArchiveManifest inspection AND standalone full-OTA partition extraction
/// (REPLACE, REPLACE_BZ, REPLACE_XZ, ZSTD, ZERO, DISCARD) with SHA-256 verification.
/// </summary>
public static class AndroidPayloadBinParser
{
    public const int OpReplace = 0;
    public const int OpReplaceBz = 1;
    public const int OpZero = 6;
    public const int OpDiscard = 7;
    public const int OpReplaceXz = 8;
    public const int OpReplaceZstd = 14;

    private sealed record PayloadExtent(ulong StartBlock, ulong NumBlocks);

    private sealed record PayloadInstallOp(
        int OperationType,
        ulong DataOffset,
        ulong DataLength,
        IReadOnlyList<PayloadExtent> DstExtents,
        byte[]? DataSha256);

    private sealed record DetailedPartitionPlan(
        string PartitionName,
        ulong NewSizeBytes,
        string? ExpectedSha256Hex,
        IReadOnlyList<PayloadInstallOp> Operations);

    private sealed record ParsedPayloadLayout(
        PayloadManifestInfo Summary,
        long BlobStartOffset,
        IReadOnlyList<DetailedPartitionPlan> DetailedPartitions);

    public static PayloadManifestInfo? TryParsePayloadHeaderAndManifest(Stream stream) =>
        TryParseDetailedLayout(stream)?.Summary;

    public static async Task<string> ExtractPartitionFromPayloadStreamAsync(
        Stream payloadStream,
        string requestedPartitionName,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payloadStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPartitionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var normalizedName = Path.GetFileNameWithoutExtension(requestedPartitionName.Trim());
        if (HashAndBinaryHelpers.IsProtectedPartition(normalizedName))
        {
            throw new SafetyPolicyViolationException(
                $"Partition '{normalizedName}' is a protected security/NVRAM partition and cannot be extracted.");
        }

        // Read header synchronously-compatible via byte[] buffers (safe for both seekable FileStream and forward-only Zip entry stream)
        var hdr = new byte[24];
        int hdrRead = await ReadExactAsync(payloadStream, hdr, 0, 20, ct).ConfigureAwait(false);
        if (hdrRead < 20 || Encoding.ASCII.GetString(hdr, 0, 4) != "CrAU")
        {
            throw new ServicingException("INVALID_PAYLOAD_MAGIC", "Stream does not begin with Android CrAU payload magic.");
        }

        ulong version = BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(4, 8));
        ulong manifestLen = BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(12, 8));
        uint sigLen = 0;
        long currentStreamPos = 20;

        if (version >= 2)
        {
            int extra = await ReadExactAsync(payloadStream, hdr, 20, 4, ct).ConfigureAwait(false);
            if (extra < 4)
            {
                throw new ServicingException("INVALID_PAYLOAD_HEADER", "Truncated CrAU v2 metadata signature length.");
            }
            sigLen = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(20, 4));
            currentStreamPos = 24;
        }

        if (manifestLen == 0 || manifestLen > 32 * 1024 * 1024)
        {
            throw new ServicingException("INVALID_PAYLOAD_MANIFEST", $"Manifest length {manifestLen} is invalid or exceeds 32 MiB safety cap.");
        }

        var manifestBytes = new byte[(int)manifestLen];
        if (await ReadExactAsync(payloadStream, manifestBytes, 0, manifestBytes.Length, ct).ConfigureAwait(false) != manifestBytes.Length)
        {
            throw new ServicingException("TRUNCATED_PAYLOAD_MANIFEST", "Unexpected EOF while reading payload.bin manifest.");
        }
        currentStreamPos += manifestBytes.Length;

        if (sigLen > 0)
        {
            await AdvanceStreamToAsync(payloadStream, currentStreamPos, currentStreamPos + sigLen, ct).ConfigureAwait(false);
            currentStreamPos += sigLen;
        }

        long blobStartOffset = currentStreamPos;
        var (blockSize, minorVer, _, detailedParts) = DecodeManifestDetailed(manifestBytes);

        var targetPlan = detailedParts.FirstOrDefault(p =>
            string.Equals(p.PartitionName, normalizedName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.PartitionName, requestedPartitionName.Trim(), StringComparison.OrdinalIgnoreCase));

        if (targetPlan is null)
        {
            var available = string.Join(", ", detailedParts.Select(p => p.PartitionName));
            throw new ServicingException(
                "PAYLOAD_PARTITION_NOT_FOUND",
                $"Partition '{normalizedName}' was not found in payload.bin. Available: [{available}].");
        }

        if (minorVer > 0 && targetPlan.Operations.Any(op => !IsStandaloneFullOperation(op.OperationType)))
        {
            throw new ServicingException(
                "INCREMENTAL_OTA_UNSUPPORTED",
                $"payload.bin uses incremental/delta minor_version={minorVer} for '{targetPlan.PartitionName}'. Standalone extraction requires a Full OTA payload.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var outPath = Path.GetFullPath(Path.Combine(destinationDirectory, $"{targetPlan.PartitionName}.img"));

        await using (var outFs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true))
        {
            if (targetPlan.NewSizeBytes > 0)
            {
                outFs.SetLength((long)targetPlan.NewSizeBytes);
            }

            foreach (var op in targetPlan.Operations)
            {
                ct.ThrowIfCancellationRequested();

                if (!IsStandaloneFullOperation(op.OperationType))
                {
                    throw new ServicingException(
                        "UNSUPPORTED_PAYLOAD_OP",
                        $"Unsupported differential InstallOperation type {op.OperationType} in '{targetPlan.PartitionName}'.");
                }

                if (op.OperationType is OpZero or OpDiscard)
                {
                    await WriteZeroExtentsAsync(outFs, op.DstExtents, blockSize, ct).ConfigureAwait(false);
                    continue;
                }

                long absoluteBlobOffset = blobStartOffset + (long)op.DataOffset;
                currentStreamPos = await AdvanceStreamToAsync(payloadStream, currentStreamPos, absoluteBlobOffset, ct).ConfigureAwait(false);

                if (op.DataLength > 512UL * 1024 * 1024)
                {
                    throw new ServicingException("PAYLOAD_CHUNK_TOO_LARGE", $"Single payload operation exceeds 512 MiB chunk safety limit ({op.DataLength} B).");
                }

                var compressedBlob = new byte[(int)op.DataLength];
                int got = await ReadExactAsync(payloadStream, compressedBlob, 0, compressedBlob.Length, ct).ConfigureAwait(false);
                if (got != compressedBlob.Length)
                {
                    throw new ServicingException("TRUNCATED_PAYLOAD_BLOB", $"Unexpected EOF reading operation blob for '{targetPlan.PartitionName}'.");
                }
                currentStreamPos += got;

                if (op.DataSha256 is { Length: 32 })
                {
                    var actualBlobHash = SHA256.HashData(compressedBlob);
                    if (!actualBlobHash.AsSpan().SequenceEqual(op.DataSha256))
                    {
                        throw new ServicingException(
                            "PAYLOAD_OP_HASH_MISMATCH",
                            $"Payload chunk SHA-256 verification failed while extracting '{targetPlan.PartitionName}'.");
                    }
                }

                await DecompressOpToExtentsAsync(op.OperationType, compressedBlob, outFs, op.DstExtents, blockSize, ct).ConfigureAwait(false);
            }

            await outFs.FlushAsync(ct).ConfigureAwait(false);
        }

        // Verify final extracted partition SHA-256 if manifest provided one
        if (!string.IsNullOrWhiteSpace(targetPlan.ExpectedSha256Hex))
        {
            var (actualHex, _) = await HashAndBinaryHelpers.ComputeHashesAsync(outPath, maxBytesToHash: 16L * 1024 * 1024 * 1024, ct: ct).ConfigureAwait(false);
            if (!string.Equals(actualHex, targetPlan.ExpectedSha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(outPath);
                throw new ServicingException(
                    "PAYLOAD_PARTITION_HASH_MISMATCH",
                    $"Extracted '{targetPlan.PartitionName}.img' SHA-256 ({actualHex}) did not match manifest ({targetPlan.ExpectedSha256Hex}).");
            }
        }

        return outPath;
    }

    private static bool IsStandaloneFullOperation(int opType) =>
        opType is OpReplace or OpReplaceBz or OpZero or OpDiscard or OpReplaceXz or OpReplaceZstd;

    private static async Task WriteZeroExtentsAsync(
        FileStream outFs,
        IReadOnlyList<PayloadExtent> extents,
        uint blockSize,
        CancellationToken ct)
    {
        var zeros = new byte[65536];
        foreach (var ext in extents)
        {
            outFs.Position = (long)(ext.StartBlock * blockSize);
            long remaining = (long)(ext.NumBlocks * blockSize);
            while (remaining > 0)
            {
                int n = (int)Math.Min(zeros.Length, remaining);
                await outFs.WriteAsync(zeros.AsMemory(0, n), ct).ConfigureAwait(false);
                remaining -= n;
            }
        }
    }

    private static async Task DecompressOpToExtentsAsync(
        int opType,
        byte[] compressedBlob,
        FileStream outFs,
        IReadOnlyList<PayloadExtent> extents,
        uint blockSize,
        CancellationToken ct)
    {
        using var memIn = new MemoryStream(compressedBlob, writable: false);
        Stream decodedStream = opType switch
        {
            OpReplace => memIn,
            OpReplaceBz => await BZip2Stream.CreateAsync(memIn, CompressionMode.Decompress, false, false, false, ct).ConfigureAwait(false),
            OpReplaceXz => new XZStream(memIn),
            OpReplaceZstd => new DecompressionStream(memIn),
            _ => throw new ServicingException("UNSUPPORTED_PAYLOAD_CODEC", $"Unsupported codec {opType}.")
        };

        await using (decodedStream.ConfigureAwait(false))
        {
            var transferBuf = new byte[65536];
            foreach (var ext in extents)
            {
                outFs.Position = (long)(ext.StartBlock * blockSize);
                long remaining = (long)(ext.NumBlocks * blockSize);
                while (remaining > 0)
                {
                    int want = (int)Math.Min(transferBuf.Length, remaining);
                    int got = await decodedStream.ReadAsync(transferBuf.AsMemory(0, want), ct).ConfigureAwait(false);
                    if (got <= 0)
                        break;
                    await outFs.WriteAsync(transferBuf.AsMemory(0, got), ct).ConfigureAwait(false);
                    remaining -= got;
                }
            }
        }
    }

    private static async Task<long> AdvanceStreamToAsync(Stream stream, long currentPos, long targetPos, CancellationToken ct)
    {
        if (targetPos == currentPos)
            return currentPos;

        if (stream.CanSeek)
        {
            stream.Position = targetPos;
            return targetPos;
        }

        if (targetPos < currentPos)
        {
            throw new ServicingException(
                "NON_SEEKABLE_PAYLOAD_BACKTRACK",
                "Payload stream inside compressed container requires backward seek; extract payload.bin first.");
        }

        long toSkip = targetPos - currentPos;
        var scratch = new byte[65536];
        while (toSkip > 0)
        {
            int chunk = (int)Math.Min(scratch.Length, toSkip);
            int r = await stream.ReadAsync(scratch.AsMemory(0, chunk), ct).ConfigureAwait(false);
            if (r <= 0)
                throw new ServicingException("TRUNCATED_PAYLOAD_STREAM", "Unexpected EOF while advancing to payload blob offset.");
            toSkip -= r;
        }
        return targetPos;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total), ct).ConfigureAwait(false);
            if (n <= 0)
                break;
            total += n;
        }
        return total;
    }

    private static ParsedPayloadLayout? TryParseDetailedLayout(Stream stream)
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
        if (stream.Read(manifestBytes, 0, manifestBytes.Length) != manifestBytes.Length)
            return null;

        var (blockSize, minorVer, summaries, detailed) = DecodeManifestDetailed(manifestBytes);
        var info = new PayloadManifestInfo(version, manifestLen, sigLen, blockSize, minorVer, summaries);
        return new ParsedPayloadLayout(info, headerSize + (long)manifestLen + sigLen, detailed);
    }

    private static (
        uint BlockSize,
        uint MinorVersion,
        List<PayloadPartitionSummary> Summaries,
        List<DetailedPartitionPlan> Detailed) DecodeManifestDetailed(ReadOnlySpan<byte> data)
    {
        uint blockSize = 4096;
        uint minorVersion = 0;
        var summaries = new List<PayloadPartitionSummary>();
        var detailed = new List<DetailedPartitionPlan>();

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
                    if (!TryReadVarint(data, ref offset, out ulong v))
                        return (blockSize, minorVersion, summaries, detailed);
                    if (fieldNumber == 3) blockSize = (uint)v;
                    else if (fieldNumber == 12) minorVersion = (uint)v;
                    break;

                case 2:
                    if (!TryReadVarint(data, ref offset, out ulong len) || offset + (int)len > data.Length)
                        return (blockSize, minorVersion, summaries, detailed);
                    var slice = data.Slice(offset, (int)len);
                    offset += (int)len;

                    if (fieldNumber == 13)
                    {
                        var (sum, det) = DecodePartitionUpdateDetailed(slice);
                        if (!string.IsNullOrWhiteSpace(sum.PartitionName))
                        {
                            summaries.Add(sum);
                            detailed.Add(det);
                        }
                    }
                    break;

                case 1:
                    offset += 8;
                    break;
                case 5:
                    offset += 4;
                    break;
                default:
                    return (blockSize, minorVersion, summaries, detailed);
            }
        }

        return (blockSize, minorVersion, summaries, detailed);
    }

    private static (PayloadPartitionSummary Summary, DetailedPartitionPlan Plan) DecodePartitionUpdateDetailed(ReadOnlySpan<byte> data)
    {
        string name = string.Empty;
        ulong sizeBytes = 0;
        string? shaHex = null;
        var ops = new List<PayloadInstallOp>();

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
                        return (new PayloadPartitionSummary(name, sizeBytes, shaHex, ops.Count), new DetailedPartitionPlan(name, sizeBytes, shaHex, ops));
                    break;

                case 2:
                    if (!TryReadVarint(data, ref offset, out ulong len) || offset + (int)len > data.Length)
                        return (new PayloadPartitionSummary(name, sizeBytes, shaHex, ops.Count), new DetailedPartitionPlan(name, sizeBytes, shaHex, ops));
                    var sub = data.Slice(offset, (int)len);
                    offset += (int)len;

                    if (fieldNumber == 1)
                    {
                        name = Encoding.UTF8.GetString(sub);
                    }
                    else if (fieldNumber == 7)
                    {
                        (sizeBytes, shaHex) = DecodePartitionInfo(sub);
                    }
                    else if (fieldNumber == 8)
                    {
                        ops.Add(DecodeInstallOperation(sub));
                    }
                    break;

                case 1:
                    offset += 8;
                    break;
                case 5:
                    offset += 4;
                    break;
                default:
                    return (new PayloadPartitionSummary(name, sizeBytes, shaHex, ops.Count), new DetailedPartitionPlan(name, sizeBytes, shaHex, ops));
            }
        }

        return (
            new PayloadPartitionSummary(name, sizeBytes, shaHex, ops.Count),
            new DetailedPartitionPlan(name, sizeBytes, shaHex, ops));
    }

    private static PayloadInstallOp DecodeInstallOperation(ReadOnlySpan<byte> data)
    {
        int opType = 0;
        ulong dataOffset = 0;
        ulong dataLen = 0;
        var dstExtents = new List<PayloadExtent>();
        byte[]? dataSha = null;

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
                if (field == 1) opType = (int)v;
                else if (field == 2) dataOffset = v;
                else if (field == 3) dataLen = v;
            }
            else if (wt == 2)
            {
                if (!TryReadVarint(data, ref offset, out ulong len) || offset + (int)len > data.Length) break;
                var sub = data.Slice(offset, (int)len);
                offset += (int)len;
                if (field == 5)
                {
                    dstExtents.Add(DecodeExtent(sub));
                }
                else if (field == 6)
                {
                    dataSha = sub.ToArray();
                }
            }
            else if (wt == 1) offset += 8;
            else if (wt == 5) offset += 4;
            else break;
        }

        return new PayloadInstallOp(opType, dataOffset, dataLen, dstExtents, dataSha);
    }

    private static PayloadExtent DecodeExtent(ReadOnlySpan<byte> data)
    {
        ulong start = 0, num = 0;
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
                if (field == 1) start = v;
                else if (field == 2) num = v;
            }
            else if (wt == 2)
            {
                if (!TryReadVarint(data, ref offset, out ulong len) || offset + (int)len > data.Length) break;
                offset += (int)len;
            }
            else if (wt == 1) offset += 8;
            else if (wt == 5) offset += 4;
            else break;
        }
        return new PayloadExtent(start, num);
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
