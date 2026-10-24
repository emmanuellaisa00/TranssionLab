using System.Buffers.Binary;
using System.Text;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Models;

namespace TranssionLab.Firmware.Parsers;

public static class AndroidSparseAndBootParsers
{
    public const uint SparseMagic = 0xED26FF3A;
    public const ushort ChunkTypeRaw = 0xCAC1;
    public const ushort ChunkTypeFill = 0xCAC2;
    public const ushort ChunkTypeDontCare = 0xCAC3;
    public const ushort ChunkTypeCrc32 = 0xCAC4;

    public const uint LpMetadataGeometryMagic = 0x616C4467;
    public const uint LpMetadataHeaderMagic = 0x414C5030;

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

    /// <summary>
    /// Converts an Android Sparse image (0xED26FF3A) into a raw block-exact .img stream.
    /// </summary>
    public static async Task<long> ConvertSparseToRawAsync(
        Stream sparseInput,
        Stream rawOutput,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sparseInput);
        ArgumentNullException.ThrowIfNull(rawOutput);

        if (!sparseInput.CanSeek || sparseInput.Length < 28)
        {
            throw new ServicingException("INVALID_SPARSE_IMAGE", "Input stream is too small or non-seekable.");
        }

        sparseInput.Position = 0;
        var hdr = new byte[28];
        if (await ReadExactAsync(sparseInput, hdr, 28, ct).ConfigureAwait(false) != 28 ||
            BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(0, 4)) != SparseMagic)
        {
            throw new ServicingException("INVALID_SPARSE_MAGIC", "File does not contain Android Sparse 0xED26FF3A magic.");
        }

        ushort fileHdrSz = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(8, 2));
        ushort chunkHdrSz = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(10, 2));
        uint blkSz = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(12, 4));
        uint totalBlks = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(16, 4));
        uint totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(20, 4));

        if (fileHdrSz < 28 || chunkHdrSz < 12 || blkSz == 0)
        {
            throw new ServicingException("INVALID_SPARSE_HEADER", "Sparse header geometry fields are invalid.");
        }

        long expectedTotalBytes = (long)totalBlks * blkSz;
        if (rawOutput.CanSeek)
        {
            rawOutput.SetLength(expectedTotalBytes);
            rawOutput.Position = 0;
        }

        sparseInput.Position = fileHdrSz;
        var chdr = new byte[chunkHdrSz];
        var copyBuf = new byte[65536];
        var zeroBuf = new byte[65536];
        var fillFour = new byte[4];

        for (uint i = 0; i < totalChunks; i++)
        {
            ct.ThrowIfCancellationRequested();
            long chunkStart = sparseInput.Position;
            if (await ReadExactAsync(sparseInput, chdr, chunkHdrSz, ct).ConfigureAwait(false) != chunkHdrSz)
            {
                throw new ServicingException("TRUNCATED_SPARSE_CHUNK", $"Unexpected EOF reading sparse chunk #{i}.");
            }

            ushort chunkType = BinaryPrimitives.ReadUInt16LittleEndian(chdr.AsSpan(0, 2));
            uint chunkBlocks = BinaryPrimitives.ReadUInt32LittleEndian(chdr.AsSpan(4, 4));
            uint totalSz = BinaryPrimitives.ReadUInt32LittleEndian(chdr.AsSpan(8, 4));
            long outBytes = (long)chunkBlocks * blkSz;

            switch (chunkType)
            {
                case ChunkTypeRaw:
                {
                    long payloadRemaining = outBytes;
                    while (payloadRemaining > 0)
                    {
                        int want = (int)Math.Min(copyBuf.Length, payloadRemaining);
                        int got = await sparseInput.ReadAsync(copyBuf.AsMemory(0, want), ct).ConfigureAwait(false);
                        if (got <= 0)
                            throw new ServicingException("TRUNCATED_SPARSE_RAW", "Truncated RAW payload inside sparse image.");
                        await rawOutput.WriteAsync(copyBuf.AsMemory(0, got), ct).ConfigureAwait(false);
                        payloadRemaining -= got;
                    }
                    break;
                }

                case ChunkTypeFill:
                {
                    if (await ReadExactAsync(sparseInput, fillFour, 4, ct).ConfigureAwait(false) != 4)
                        throw new ServicingException("TRUNCATED_SPARSE_FILL", "Missing 4-byte fill pattern.");

                    // Populate copyBuf with repeated 4-byte fill value
                    for (int pos = 0; pos < copyBuf.Length; pos += 4)
                    {
                        Buffer.BlockCopy(fillFour, 0, copyBuf, pos, 4);
                    }

                    long rem = outBytes;
                    while (rem > 0)
                    {
                        int n = (int)Math.Min(copyBuf.Length, rem);
                        await rawOutput.WriteAsync(copyBuf.AsMemory(0, n), ct).ConfigureAwait(false);
                        rem -= n;
                    }
                    break;
                }

                case ChunkTypeDontCare:
                {
                    if (rawOutput.CanSeek)
                    {
                        rawOutput.Seek(outBytes, SeekOrigin.Current);
                    }
                    else
                    {
                        long rem = outBytes;
                        while (rem > 0)
                        {
                            int n = (int)Math.Min(zeroBuf.Length, rem);
                            await rawOutput.WriteAsync(zeroBuf.AsMemory(0, n), ct).ConfigureAwait(false);
                            rem -= n;
                        }
                    }
                    break;
                }

                case ChunkTypeCrc32:
                    break;

                default:
                    throw new ServicingException("UNKNOWN_SPARSE_CHUNK", $"Unknown sparse chunk type 0x{chunkType:X4}.");
            }

            sparseInput.Position = chunkStart + totalSz;
        }

        await rawOutput.FlushAsync(ct).ConfigureAwait(false);
        return expectedTotalBytes;
    }

    /// <summary>
    /// Parses Android Dynamic Partition LpMetadata (super.img) from either a raw super.img
    /// or the initial RAW chunk of a sparse super.img!
    /// </summary>
    public static SuperMetadataInfo? TryParseSuperLpMetadata(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 512)
            return null;

        // Grab up to the first 128 KiB of effective super image bytes (either directly or from first sparse RAW chunk)
        var probe = ExtractLeadingSuperBytes(stream, 131072);
        if (probe is null || probe.Length < 0x3000)
            return null;

        ReadOnlySpan<byte> data = probe;
        // LpMetadataGeometry (`0x616C4467`) lives at offset 0x1000 (standard LP_PARTITION_RESERVED_BYTES = 4096) or 0x0 (super_empty.img)
        int geomOffset = -1;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x1000, 4)) == LpMetadataGeometryMagic)
            geomOffset = 0x1000;
        else if (BinaryPrimitives.ReadUInt32LittleEndian(data[..4]) == LpMetadataGeometryMagic)
            geomOffset = 0;

        if (geomOffset < 0)
            return null;

        // LpMetadataGeometry is padded to LP_METADATA_GEOMETRY_SIZE = 4096 bytes, duplicated twice (primary + backup = 8192 bytes)
        uint metadataMaxSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(geomOffset + 40, 4));
        uint slotCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(geomOffset + 44, 4));

        // Search for LpMetadataHeaderMagic (0x414C5030) right after geometry (geomOffset + 0x2000 or geomOffset + 0x1000)
        int hdrOffset = -1;
        int[] candidateOffsets = [geomOffset + 0x2000, geomOffset + 0x1000, geomOffset + 52];
        foreach (var cand in candidateOffsets)
        {
            if (cand >= 0 && cand + 128 <= data.Length &&
                BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cand, 4)) == LpMetadataHeaderMagic)
            {
                hdrOffset = cand;
                break;
            }
        }

        if (hdrOffset < 0)
            return null;

        var hdr = data[hdrOffset..];
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(4, 2));
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(6, 2));
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(8, 4));
        if (headerSize < 128 || hdrOffset + headerSize > data.Length)
            return null;

        // LpMetadataTableDescriptor (12 bytes each: uint32 offset, uint32 num_entries, uint32 entry_size) starting at hdr + 76:
        // [0] partitions (offset 76), [1] extents (offset 88), [2] groups (offset 100), [3] block_devices (offset 112)
        var (partOff, partCount, partEntrySz) = ReadTableDesc(hdr.Slice(76, 12));
        var (extOff, extCount, extEntrySz) = ReadTableDesc(hdr.Slice(88, 12));
        var (grpOff, grpCount, grpEntrySz) = ReadTableDesc(hdr.Slice(100, 12));
        var (blkOff, blkCount, blkEntrySz) = ReadTableDesc(hdr.Slice(112, 12));

        int tablesBase = hdrOffset + (int)headerSize;

        // Parse groups (48 bytes: char name[36], uint32 flags, uint64 maximum_size)
        var groups = new List<string>();
        if (grpEntrySz >= 48 && grpCount <= 64 && tablesBase + grpOff + grpCount * grpEntrySz <= data.Length)
        {
            for (int i = 0; i < grpCount; i++)
            {
                var gSlice = data.Slice(tablesBase + (int)grpOff + i * (int)grpEntrySz, (int)grpEntrySz);
                groups.Add(ReadNullTerminatedAscii(gSlice[..36]));
            }
        }

        // Parse extents (24 bytes: uint64 num_sectors, uint32 target_type, uint64 target_data, uint32 target_source)
        var extentSectors = new List<ulong>();
        if (extEntrySz >= 24 && extCount <= 4096 && tablesBase + extOff + extCount * extEntrySz <= data.Length)
        {
            for (int i = 0; i < extCount; i++)
            {
                var eSlice = data.Slice(tablesBase + (int)extOff + i * (int)extEntrySz, (int)extEntrySz);
                extentSectors.Add(BinaryPrimitives.ReadUInt64LittleEndian(eSlice[..8]));
            }
        }

        // Parse block_devices[0] size if present (64 bytes: uint64 first_logical_sector, uint32 alignment, uint32 alignment_offset, uint64 size, char partition_name[36], uint32 flags)
        ulong superDevBytes = metadataMaxSize;
        if (blkEntrySz >= 24 && blkCount >= 1 && tablesBase + blkOff + blkEntrySz <= data.Length)
        {
            var bSlice = data.Slice(tablesBase + (int)blkOff, (int)blkEntrySz);
            superDevBytes = BinaryPrimitives.ReadUInt64LittleEndian(bSlice.Slice(16, 8));
        }

        // Parse partitions (52 bytes: char name[36], uint32 attributes, uint32 first_extent_index, uint32 num_extents, uint32 group_index)
        var logicalParts = new List<SuperLogicalPartitionEntry>();
        if (partEntrySz >= 52 && partCount <= 256 && tablesBase + partOff + partCount * partEntrySz <= data.Length)
        {
            for (int i = 0; i < partCount; i++)
            {
                var pSlice = data.Slice(tablesBase + (int)partOff + i * (int)partEntrySz, (int)partEntrySz);
                var pName = ReadNullTerminatedAscii(pSlice[..36]);
                uint attrs = BinaryPrimitives.ReadUInt32LittleEndian(pSlice.Slice(36, 4));
                uint firstExt = BinaryPrimitives.ReadUInt32LittleEndian(pSlice.Slice(40, 4));
                uint numExt = BinaryPrimitives.ReadUInt32LittleEndian(pSlice.Slice(44, 4));
                uint grpIdx = BinaryPrimitives.ReadUInt32LittleEndian(pSlice.Slice(48, 4));

                ulong totalSectors = 0;
                for (uint e = 0; e < numExt && firstExt + e < extentSectors.Count; e++)
                {
                    totalSectors += extentSectors[(int)(firstExt + e)];
                }

                var grpName = grpIdx < groups.Count ? groups[(int)grpIdx] : $"group_{grpIdx}";
                if (!string.IsNullOrWhiteSpace(pName))
                {
                    logicalParts.Add(new SuperLogicalPartitionEntry(
                        pName,
                        grpName,
                        attrs,
                        (long)(totalSectors * 512UL),
                        (int)numExt));
                }
            }
        }

        return new SuperMetadataInfo(major, minor, superDevBytes, slotCount, groups, logicalParts);
    }

    private static (uint Offset, uint Count, uint EntrySize) ReadTableDesc(ReadOnlySpan<byte> span) =>
        (BinaryPrimitives.ReadUInt32LittleEndian(span[..4]),
         BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4)),
         BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4)));

    private static byte[]? ExtractLeadingSuperBytes(Stream stream, int maxBytes)
    {
        stream.Position = 0;
        Span<byte> hdr = stackalloc byte[28];
        if (stream.Read(hdr) < 28)
            return null;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr[..4]);
        if (magic != SparseMagic)
        {
            stream.Position = 0;
            int toRead = (int)Math.Min(maxBytes, stream.Length);
            var raw = new byte[toRead];
            int got = stream.Read(raw, 0, toRead);
            return got > 0 ? raw[..got] : null;
        }

        // Sparse super.img: reconstruct the first maxBytes from leading chunks!
        ushort fileHdrSz = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(8, 2));
        ushort chunkHdrSz = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(10, 2));
        uint blkSz = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(12, 4));
        uint totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(20, 4));
        if (fileHdrSz < 28 || chunkHdrSz < 12 || blkSz == 0)
            return null;

        var assembled = new byte[maxBytes];
        int written = 0;
        stream.Position = fileHdrSz;
        Span<byte> chdr = stackalloc byte[12];

        for (uint i = 0; i < totalChunks && written < maxBytes; i++)
        {
            long cStart = stream.Position;
            if (stream.Position + chunkHdrSz > stream.Length || stream.Read(chdr) != 12)
                break;

            ushort cType = BinaryPrimitives.ReadUInt16LittleEndian(chdr[..2]);
            uint cBlks = BinaryPrimitives.ReadUInt32LittleEndian(chdr.Slice(4, 4));
            uint cTotalSz = BinaryPrimitives.ReadUInt32LittleEndian(chdr.Slice(8, 4));
            long chunkExpanded = (long)cBlks * blkSz;

            stream.Position = cStart + chunkHdrSz;
            int take = (int)Math.Min(maxBytes - written, chunkExpanded);
            if (cType == ChunkTypeRaw && take > 0)
            {
                int r = stream.Read(assembled, written, take);
                written += Math.Max(0, r);
            }
            else if (cType is ChunkTypeDontCare or ChunkTypeFill)
            {
                written += take;
            }

            stream.Position = cStart + cTotalSz;
        }

        return written > 0 ? assembled[..written] : null;
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
            uint headerVersion = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(40, 4));
            if (headerVersion >= 3 && headerVersion <= 4)
            {
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

    private static async Task<int> ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        var slice = nul >= 0 ? bytes[..nul] : bytes;
        return Encoding.ASCII.GetString(slice).Trim();
    }
}
