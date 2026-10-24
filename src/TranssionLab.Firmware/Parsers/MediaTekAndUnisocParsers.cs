using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Models;

namespace TranssionLab.Firmware.Parsers;

public static class MediaTekAndUnisocParsers
{
    public static (MediaTekScatterInfo Info, List<PartitionImageMetadata> Partitions, List<string> Warnings) ParseMediaTekScatter(
        string scatterFilePath)
    {
        var text = File.ReadAllText(scatterFilePath, Encoding.UTF8);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(scatterFilePath)) ?? string.Empty;

        if (text.TrimStart().StartsWith('<'))
        {
            return ParseScatterXml(text, baseDir);
        }

        return ParseScatterText(text, baseDir);
    }

    private static (MediaTekScatterInfo, List<PartitionImageMetadata>, List<string>) ParseScatterText(string text, string baseDir)
    {
        string platform = "Unknown", project = "Unknown", storage = "UFS/eMMC", version = "V1.1";
        var partitions = new List<PartitionImageMetadata>();
        var warnings = new List<string>();
        int missingFiles = 0;

        Dictionary<string, string>? currentBlock = null;
        foreach (var rawLine in text.Split(['\r', '\n']))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            if (line.StartsWith("- partition_index:", StringComparison.OrdinalIgnoreCase))
            {
                if (currentBlock is not null)
                    FlushBlock(currentBlock, baseDir, partitions, warnings, ref missingFiles);
                currentBlock = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                currentBlock["partition_index"] = line.Split(':', 2)[1].Trim();
                continue;
            }

            var kv = line.Split(':', 2);
            if (kv.Length != 2) continue;
            var k = kv[0].Trim().TrimStart('-').Trim();
            var v = kv[1].Trim();

            if (currentBlock is null)
            {
                if (k.Equals("platform", StringComparison.OrdinalIgnoreCase)) platform = v;
                else if (k.Equals("project", StringComparison.OrdinalIgnoreCase)) project = v;
                else if (k.Equals("storage", StringComparison.OrdinalIgnoreCase)) storage = v;
                else if (k.Equals("config_version", StringComparison.OrdinalIgnoreCase)) version = v;
            }
            else
            {
                currentBlock[k] = v;
            }
        }

        if (currentBlock is not null)
            FlushBlock(currentBlock, baseDir, partitions, warnings, ref missingFiles);

        return (
            new MediaTekScatterInfo(platform, project, storage, version, partitions.Count, missingFiles),
            partitions,
            warnings);
    }

    private static void FlushBlock(
        Dictionary<string, string> block,
        string baseDir,
        List<PartitionImageMetadata> list,
        List<string> warnings,
        ref int missingFiles)
    {
        if (!block.TryGetValue("partition_name", out var partName) || string.IsNullOrWhiteSpace(partName))
            return;

        block.TryGetValue("file_name", out var fileName);
        fileName ??= "NONE";
        block.TryGetValue("physical_start_addr", out var startAddr);
        block.TryGetValue("partition_size", out var partSizeHex);
        block.TryGetValue("is_download", out var isDlStr);

        bool isDownload = string.Equals(isDlStr, "true", StringComparison.OrdinalIgnoreCase);
        long? declaredCap = TryParseHexOrDec(partSizeHex);
        bool isProtected = HashAndBinaryHelpers.IsProtectedPartition(partName);

        long fileBytes = 0;
        long effectiveBytes = 0;
        FirmwareFormatKind fmt = FirmwareFormatKind.RawPartitionImage;
        string summary = isDownload ? "Referenced scatter image" : "Non-download metadata/runtime partition";

        if (isDownload && !string.Equals(fileName, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(baseDir, fileName);
            if (File.Exists(candidate))
            {
                var fi = new FileInfo(candidate);
                fileBytes = fi.Length;
                effectiveBytes = fi.Length;
                using var fs = File.OpenRead(candidate);
                (fmt, summary) = HashAndBinaryHelpers.SniffStreamMagic(fs, fileName);
                if (fmt == FirmwareFormatKind.AndroidSparseImage)
                {
                    var sp = AndroidSparseAndBootParsers.TryParseSparseImage(fs);
                    if (sp is not null) effectiveBytes = sp.TotalUnsparsedBytes;
                }

                if (declaredCap.HasValue && declaredCap.Value > 0 && effectiveBytes > declaredCap.Value)
                {
                    warnings.Add($"Image '{fileName}' effective size ({effectiveBytes} B) exceeds scatter partition '{partName}' capacity ({declaredCap.Value} B).");
                }
            }
            else
            {
                missingFiles++;
                warnings.Add($"Scatter partition '{partName}' references missing image '{fileName}'.");
            }
        }

        list.Add(new PartitionImageMetadata(
            PartitionName: partName,
            FileName: fileName,
            FileSizeBytes: fileBytes,
            EffectiveUnsparsedBytes: effectiveBytes,
            ImageFormat: fmt,
            Sha256Hex: null,
            PhysicalStartAddressHex: startAddr,
            DeclaredPartitionCapacityBytes: declaredCap,
            IsDownloadSelectedByDefault: isDownload && !isProtected,
            IsProtectedNvOrRadio: isProtected,
            FormatSummary: summary));
    }

    private static (MediaTekScatterInfo, List<PartitionImageMetadata>, List<string>) ParseScatterXml(string xmlText, string baseDir)
    {
        var doc = XDocument.Parse(xmlText);
        var root = doc.Root;
        string platform = root?.Element("general")?.Element("platform")?.Value ?? root?.Attribute("platform")?.Value ?? "MediaTek XML";
        string project = root?.Element("general")?.Element("project")?.Value ?? "Unknown";
        string storage = root?.Element("storage_type")?.Attribute("name")?.Value ?? "UFS";
        var partitions = new List<PartitionImageMetadata>();
        var warnings = new List<string>();
        int missing = 0;

        foreach (var pt in doc.Descendants().Where(e => e.Name.LocalName is "partition_index" or "entry"))
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in pt.Elements())
            {
                dict[child.Name.LocalName] = child.Value.Trim();
            }
            foreach (var attr in pt.Attributes())
            {
                dict[attr.Name.LocalName] = attr.Value.Trim();
            }
            FlushBlock(dict, baseDir, partitions, warnings, ref missing);
        }

        return (new MediaTekScatterInfo(platform, project, storage, "XML-V2", partitions.Count, missing), partitions, warnings);
    }

    private static long? TryParseHexOrDec(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return hex;
        }
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : null;
    }

    // --- Spreadtrum / Unisoc Binary PAC Container Parser ---
    // Standard PAC_HEADER_T = 2124 bytes; each FILE_T = 2580 bytes.
    public const int PacHeaderSize = 2124;
    public const int PacFileEntrySize = 2580;

    public static (UnisocPacHeaderInfo? Header, List<PartitionImageMetadata> Partitions, List<PacSliceOffset> Slices, List<string> Warnings)
        ParseUnisocPac(Stream stream)
    {
        var warnings = new List<string>();
        var partitions = new List<PartitionImageMetadata>();
        var slices = new List<PacSliceOffset>();

        if (!stream.CanSeek || stream.Length < PacHeaderSize)
        {
            warnings.Add("File is smaller than the 2124-byte Unisoc PAC header.");
            return (null, partitions, slices, warnings);
        }

        stream.Position = 0;
        var hdr = new byte[PacHeaderSize];
        if (stream.Read(hdr, 0, PacHeaderSize) != PacHeaderSize)
            return (null, partitions, slices, warnings);

        string szVersion = ReadUtf16Fixed(hdr.AsSpan(0, 48)); // 24 wchar_t = 48 bytes
        uint dwHiSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(48, 4));
        uint dwLoSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(52, 4));
        long declaredTotalSize = szVersion.Contains("2.") ? ((long)dwHiSize << 32) | dwLoSize : dwHiSize;
        if (declaredTotalSize == 0) declaredTotalSize = dwLoSize;

        string prdName = ReadUtf16Fixed(hdr.AsSpan(56, 512));    // 256 wchar_t
        string prdVersion = ReadUtf16Fixed(hdr.AsSpan(568, 512)); // 256 wchar_t
        int fileCount = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(1080, 4));
        uint filesTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(1084, 4));

        if (fileCount < 0 || fileCount > 512 || filesTableOffset + (long)fileCount * PacFileEntrySize > stream.Length)
        {
            warnings.Add($"Invalid PAC entry table bounds (count={fileCount}, offset={filesTableOffset}).");
            return (null, partitions, slices, warnings);
        }

        stream.Position = filesTableOffset;
        var entryBuf = new byte[PacFileEntrySize];
        bool hasFdl1 = false, hasFdl2 = false;

        for (int i = 0; i < fileCount; i++)
        {
            if (stream.Read(entryBuf, 0, PacFileEntrySize) != PacFileEntrySize)
                break;

            string fileId = ReadUtf16Fixed(entryBuf.AsSpan(4, 512));
            string fileName = ReadUtf16Fixed(entryBuf.AsSpan(516, 512));
            uint hiFileSize = BinaryPrimitives.ReadUInt32LittleEndian(entryBuf.AsSpan(1540, 4));
            uint hiDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(entryBuf.AsSpan(1544, 4));
            uint loFileSize = BinaryPrimitives.ReadUInt32LittleEndian(entryBuf.AsSpan(1548, 4));
            int nFileFlag = BinaryPrimitives.ReadInt32LittleEndian(entryBuf.AsSpan(1552, 4));
            uint loDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(entryBuf.AsSpan(1560, 4));

            long size = ((long)hiFileSize << 32) | loFileSize;
            long dataOffset = ((long)hiDataOffset << 32) | loDataOffset;

            if (fileId.Equals("FDL", StringComparison.OrdinalIgnoreCase) || fileName.Contains("fdl1", StringComparison.OrdinalIgnoreCase))
                hasFdl1 = size > 0;
            if (fileId.Equals("FDL2", StringComparison.OrdinalIgnoreCase) || fileName.Contains("fdl2", StringComparison.OrdinalIgnoreCase))
                hasFdl2 = size > 0;

            bool isProtected = HashAndBinaryHelpers.IsProtectedPartition(fileId);
            if (size > 0 && dataOffset + size <= stream.Length)
            {
                slices.Add(new PacSliceOffset(fileId, string.IsNullOrWhiteSpace(fileName) ? $"{fileId}.bin" : fileName, dataOffset, size));
            }
            else if (size > 0)
            {
                warnings.Add($"PAC entry '{fileId}' offset/size ({dataOffset}+{size}) exceeds container length ({stream.Length}).");
            }

            partitions.Add(new PartitionImageMetadata(
                PartitionName: string.IsNullOrWhiteSpace(fileId) ? $"Entry_{i}" : fileId,
                FileName: string.IsNullOrWhiteSpace(fileName) ? "(metadata)" : fileName,
                FileSizeBytes: size,
                EffectiveUnsparsedBytes: size,
                ImageFormat: FirmwareFormatKind.RawPartitionImage,
                Sha256Hex: null,
                PhysicalStartAddressHex: $"0x{dataOffset:X}",
                DeclaredPartitionCapacityBytes: null,
                IsDownloadSelectedByDefault: nFileFlag != 0 && !isProtected && size > 0,
                IsProtectedNvOrRadio: isProtected,
                FormatSummary: size > 0 ? $"Unisoc PAC Embedded Image (Flag={nFileFlag})" : "PAC Configuration / Operation Step"));
        }

        var pacInfo = new UnisocPacHeaderInfo(
            szVersion,
            prdName,
            prdVersion,
            fileCount,
            declaredTotalSize > 0 ? declaredTotalSize : stream.Length,
            hasFdl1,
            hasFdl2);

        return (pacInfo, partitions, slices, warnings);
    }

    private static string ReadUtf16Fixed(ReadOnlySpan<byte> bytes)
    {
        var str = Encoding.Unicode.GetString(bytes);
        int nul = str.IndexOf('\0');
        return (nul >= 0 ? str[..nul] : str).Trim();
    }
}

public sealed record PacSliceOffset(string FileId, string FileName, long Offset, long Length);
