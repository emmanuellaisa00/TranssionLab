using System.IO.Compression;
using System.Text;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Models;

namespace TranssionLab.Firmware.Parsers;

public static class TarAndZipArchiveInspector
{
    public static (
        FirmwareFormatKind SpecificKind,
        List<FirmwareArchiveEntry> Entries,
        List<PartitionImageMetadata> Partitions,
        PayloadManifestInfo? EmbeddedPayload,
        string? OtaDeviceHint,
        string? OtaFingerprintHint,
        List<string> Warnings) InspectZip(string zipPath)
    {
        var entries = new List<FirmwareArchiveEntry>();
        var partitions = new List<PartitionImageMetadata>();
        var warnings = new List<string>();
        PayloadManifestInfo? embeddedPayload = null;
        string? deviceHint = null, fpHint = null;
        bool isOta = false;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                continue;

            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            var innerKind = entry.Name.Equals("payload.bin", StringComparison.OrdinalIgnoreCase)
                ? FirmwareFormatKind.AndroidPayloadBin
                : entry.Name.Contains("scatter", StringComparison.OrdinalIgnoreCase) && ext is ".txt" or ".xml"
                    ? FirmwareFormatKind.MediaTekScatterText
                    : ext == ".img"
                        ? FirmwareFormatKind.RawPartitionImage
                        : FirmwareFormatKind.Unsupported;

            entries.Add(new FirmwareArchiveEntry(
                entry.FullName,
                entry.CompressedLength,
                entry.Length,
                $"{entry.Crc32:x8}",
                entry.LastWriteTime,
                innerKind));

            if (entry.FullName.Equals("META-INF/com/android/metadata", StringComparison.OrdinalIgnoreCase))
            {
                isOta = true;
                using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
                var metaText = sr.ReadToEnd();
                foreach (var line in metaText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("pre-device=", StringComparison.OrdinalIgnoreCase))
                        deviceHint = line["pre-device=".Length..].Trim();
                    else if (line.StartsWith("post-build=", StringComparison.OrdinalIgnoreCase))
                        fpHint = line["post-build=".Length..].Trim();
                }
            }
            else if (entry.Name.Equals("android-info.txt", StringComparison.OrdinalIgnoreCase))
            {
                using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
                var info = sr.ReadToEnd();
                foreach (var line in info.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("require board=", StringComparison.OrdinalIgnoreCase))
                        deviceHint ??= line["require board=".Length..].Trim();
                }
            }
            else if (entry.Name.Equals("payload.bin", StringComparison.OrdinalIgnoreCase))
            {
                isOta = true;
                using var ms = new MemoryStream();
                using (var es = entry.Open())
                {
                    // Copy up to first 8 MiB of payload.bin so even large 40-partition manifests parse without unpacking multi-GB payloads
                    const int maxHeaderBytes = 8 * 1024 * 1024;
                    var buf = new byte[65536];
                    int total = 0;
                    int r;
                    while (total < maxHeaderBytes && (r = es.Read(buf, 0, Math.Min(buf.Length, maxHeaderBytes - total))) > 0)
                    {
                        ms.Write(buf, 0, r);
                        total += r;
                    }
                }
                ms.Position = 0;
                embeddedPayload = AndroidPayloadBinParser.TryParsePayloadHeaderAndManifest(ms);
                if (embeddedPayload is not null)
                {
                    foreach (var p in embeddedPayload.Partitions)
                    {
                        partitions.Add(new PartitionImageMetadata(
                            p.PartitionName,
                            "payload.bin",
                            (long)p.NewPartitionBytes,
                            (long)p.NewPartitionBytes,
                            FirmwareFormatKind.AndroidPayloadBin,
                            p.Sha256Hex,
                            null,
                            null,
                            !HashAndBinaryHelpers.IsProtectedPartition(p.PartitionName),
                            HashAndBinaryHelpers.IsProtectedPartition(p.PartitionName),
                            $"OTA Payload Partition ({p.InstallOperationCount} ops)"));
                    }
                }
            }
            else if (ext == ".img")
            {
                var partName = Path.GetFileNameWithoutExtension(entry.Name);
                partitions.Add(new PartitionImageMetadata(
                    partName,
                    entry.FullName,
                    entry.Length,
                    entry.Length,
                    FirmwareFormatKind.RawPartitionImage,
                    null,
                    null,
                    null,
                    !HashAndBinaryHelpers.IsProtectedPartition(partName),
                    HashAndBinaryHelpers.IsProtectedPartition(partName),
                    $"Archived Partition Image (CRC32={entry.Crc32:x8})"));
            }
        }

        return (
            isOta ? FirmwareFormatKind.AndroidOtaZip : FirmwareFormatKind.ZipArchive,
            entries,
            partitions,
            embeddedPayload,
            deviceHint,
            fpHint,
            warnings);
    }

    public static (List<FirmwareArchiveEntry> Entries, List<PartitionImageMetadata> Partitions, List<string> Warnings)
        InspectTarOrTarGz(string filePath, bool isGzip)
    {
        var entries = new List<FirmwareArchiveEntry>();
        var partitions = new List<PartitionImageMetadata>();
        var warnings = new List<string>();

        using var fs = File.OpenRead(filePath);
        using Stream tarStream = isGzip ? new GZipStream(fs, CompressionMode.Decompress) : fs;

        var block = new byte[512];
        while (true)
        {
            int read = ReadExactOrEof(tarStream, block);
            if (read == 0)
                break;
            if (read < 512)
            {
                warnings.Add("Truncated TAR header block encountered at end of stream.");
                break;
            }

            if (block.All(b => b == 0))
                break;

            var name = ReadNullAscii(block.AsSpan(0, 100));
            var prefix = ReadNullAscii(block.AsSpan(345, 155));
            var fullName = string.IsNullOrWhiteSpace(prefix) ? name : $"{prefix}/{name}";

            var sizeOctal = ReadNullAscii(block.AsSpan(124, 12)).Trim();
            long size = 0;
            if (!string.IsNullOrWhiteSpace(sizeOctal))
            {
                try { size = Convert.ToInt64(sizeOctal, 8); }
                catch { warnings.Add($"Invalid octal size '{sizeOctal}' on TAR entry '{fullName}'."); break; }
            }

            byte typeFlag = block[156];
            bool isDirectory = typeFlag == (byte)'5' || fullName.EndsWith('/');

            if (!isDirectory && !string.IsNullOrWhiteSpace(fullName))
            {
                var ext = Path.GetExtension(fullName).ToLowerInvariant();
                var kind = ext == ".img" ? FirmwareFormatKind.RawPartitionImage : FirmwareFormatKind.Unsupported;
                entries.Add(new FirmwareArchiveEntry(fullName, size, size, null, null, kind));

                if (ext == ".img")
                {
                    var partName = Path.GetFileNameWithoutExtension(fullName);
                    partitions.Add(new PartitionImageMetadata(
                        partName,
                        fullName,
                        size,
                        size,
                        FirmwareFormatKind.RawPartitionImage,
                        null,
                        null,
                        null,
                        !HashAndBinaryHelpers.IsProtectedPartition(partName),
                        HashAndBinaryHelpers.IsProtectedPartition(partName),
                        "TAR Archived Partition Image"));
                }
            }

            long paddedBytes = ((size + 511) / 512) * 512;
            SkipBytes(tarStream, paddedBytes);
        }

        return (entries, partitions, warnings);
    }

    public static async Task<string> ExtractEntrySafeAsync(
        string packagePath,
        FirmwareFormatKind format,
        string entryName,
        string destinationDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDir);
        var fullDestDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDir)) + Path.DirectorySeparatorChar;
        var safeFileName = Path.GetFileName(entryName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            throw new ServicingException("INVALID_ENTRY_NAME", $"Cannot extract invalid archive entry '{entryName}'.");
        }

        var outputPath = Path.GetFullPath(Path.Combine(destinationDir, safeFileName));
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!outputPath.StartsWith(fullDestDir, pathComparison))
        {
            throw new SafetyPolicyViolationException("Blocked path traversal attempt during archive extraction.");
        }

        var basePartName = Path.GetFileNameWithoutExtension(safeFileName);
        if (HashAndBinaryHelpers.IsProtectedPartition(basePartName))
        {
            throw new SafetyPolicyViolationException($"Blocked extraction of protected security/NVRAM partition '{basePartName}'.");
        }

        if (format == FirmwareFormatKind.AndroidPayloadBin)
        {
            await using var pfs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            return await AndroidPayloadBinParser.ExtractPartitionFromPayloadStreamAsync(pfs, basePartName, destinationDir, ct).ConfigureAwait(false);
        }

        if (format is FirmwareFormatKind.ZipArchive or FirmwareFormatKind.AndroidOtaZip)
        {
            using var zip = ZipFile.OpenRead(packagePath);
            var target = zip.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, entryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Name, $"{basePartName}.img", StringComparison.OrdinalIgnoreCase));

            if (target is not null && !target.Name.Equals("payload.bin", StringComparison.OrdinalIgnoreCase))
            {
                var finalOut = Path.HasExtension(outputPath) ? outputPath : outputPath + ".img";
                await using var src = target.Open();
                await using var dst = File.Create(finalOut);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                return finalOut;
            }

            // Check if this is an A/B OTA ZIP with embedded payload.bin!
            var payloadEntry = zip.Entries.FirstOrDefault(e => e.Name.Equals("payload.bin", StringComparison.OrdinalIgnoreCase));
            if (payloadEntry is not null && !entryName.Equals("payload.bin", StringComparison.OrdinalIgnoreCase))
            {
                await using var payloadStream = payloadEntry.Open();
                return await AndroidPayloadBinParser.ExtractPartitionFromPayloadStreamAsync(
                    payloadStream,
                    basePartName,
                    destinationDir,
                    ct).ConfigureAwait(false);
            }

            if (target is not null)
            {
                await using var src = target.Open();
                await using var dst = File.Create(outputPath);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                return outputPath;
            }

            throw new ServicingException("ENTRY_NOT_FOUND", $"Entry '{entryName}' not found in ZIP archive or embedded payload.bin.");
        }

        if (format is FirmwareFormatKind.TarArchive or FirmwareFormatKind.TarGzipArchive)
        {
            await using var fs = File.OpenRead(packagePath);
            await using Stream tarStream = format == FirmwareFormatKind.TarGzipArchive
                ? new GZipStream(fs, CompressionMode.Decompress)
                : fs;

            var block = new byte[512];
            while (ReadExactOrEof(tarStream, block) == 512 && !block.All(b => b == 0))
            {
                var name = ReadNullAscii(block.AsSpan(0, 100));
                var prefix = ReadNullAscii(block.AsSpan(345, 155));
                var fullName = string.IsNullOrWhiteSpace(prefix) ? name : $"{prefix}/{name}";
                var sizeOctal = ReadNullAscii(block.AsSpan(124, 12)).Trim();
                long size = string.IsNullOrWhiteSpace(sizeOctal) ? 0 : Convert.ToInt64(sizeOctal, 8);

                if (string.Equals(fullName, entryName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(fullName), entryName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(fullName), basePartName, StringComparison.OrdinalIgnoreCase))
                {
                    var finalOut = Path.HasExtension(outputPath) ? outputPath : outputPath + ".img";
                    await using var dst = File.Create(finalOut);
                    long remaining = size;
                    var copyBuf = new byte[65536];
                    while (remaining > 0)
                    {
                        int chunk = (int)Math.Min(copyBuf.Length, remaining);
                        int got = await tarStream.ReadAsync(copyBuf.AsMemory(0, chunk), ct).ConfigureAwait(false);
                        if (got <= 0) break;
                        await dst.WriteAsync(copyBuf.AsMemory(0, got), ct).ConfigureAwait(false);
                        remaining -= got;
                    }
                    return finalOut;
                }

                long padded = ((size + 511) / 512) * 512;
                SkipBytes(tarStream, padded);
            }

            throw new ServicingException("ENTRY_NOT_FOUND", $"Entry '{entryName}' not found in TAR container.");
        }

        if (format == FirmwareFormatKind.UnisocPacContainer)
        {
            await using var fs = File.OpenRead(packagePath);
            var (_, _, slices, _) = MediaTekAndUnisocParsers.ParseUnisocPac(fs);
            var slice = slices.FirstOrDefault(s =>
                string.Equals(s.FileId, entryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.FileName, entryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.FileId, basePartName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ServicingException("PAC_ENTRY_NOT_FOUND", $"Entry '{entryName}' not found or has zero payload in PAC container.");

            var finalOut = Path.HasExtension(outputPath) ? outputPath : Path.Combine(destinationDir, slice.FileName);
            fs.Position = slice.Offset;
            await using var dst = File.Create(finalOut);
            long remaining = slice.Length;
            var copyBuf = new byte[65536];
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(copyBuf.Length, remaining);
                int got = await fs.ReadAsync(copyBuf.AsMemory(0, chunk), ct).ConfigureAwait(false);
                if (got <= 0) break;
                await dst.WriteAsync(copyBuf.AsMemory(0, got), ct).ConfigureAwait(false);
                remaining -= got;
            }
            return finalOut;
        }

        if (format is FirmwareFormatKind.MediaTekScatterText or FirmwareFormatKind.MediaTekScatterXml)
        {
            var (_, scatterParts, _) = MediaTekAndUnisocParsers.ParseMediaTekScatter(packagePath);
            var matched = scatterParts.FirstOrDefault(p =>
                string.Equals(p.PartitionName, entryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.FileName, entryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.PartitionName, basePartName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ServicingException("SCATTER_PARTITION_NOT_FOUND", $"Partition '{entryName}' not found in scatter descriptor.");

            var scatterDir = Path.GetDirectoryName(Path.GetFullPath(packagePath)) ?? string.Empty;
            var srcImg = Path.Combine(scatterDir, Path.GetFileName(matched.FileName));
            if (!File.Exists(srcImg))
            {
                throw new ServicingException("SCATTER_IMAGE_MISSING", $"Referenced scatter image file does not exist: '{srcImg}'.");
            }

            var finalOut = Path.Combine(destinationDir, Path.GetFileName(matched.FileName));
            await using var src = File.OpenRead(srcImg);
            await using var dst = File.Create(finalOut);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            return finalOut;
        }

        throw new ServicingException("EXTRACTION_UNSUPPORTED", $"Single-entry extraction is not applicable to format '{format}'.");
    }

    private static int ReadExactOrEof(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private static void SkipBytes(Stream stream, long count)
    {
        if (count <= 0) return;
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }
        var scratch = new byte[16384];
        long left = count;
        while (left > 0)
        {
            int n = stream.Read(scratch, 0, (int)Math.Min(scratch.Length, left));
            if (n <= 0) break;
            left -= n;
        }
    }

    private static string ReadNullAscii(ReadOnlySpan<byte> span)
    {
        int nul = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul >= 0 ? span[..nul] : span).Trim();
    }
}
