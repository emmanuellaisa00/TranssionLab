using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Firmware;
using TranssionLab.Firmware.Parsers;
using TranssionLab.Logging;
using TranssionLab.Profiles;
using Xunit;

namespace TranssionLab.Tests;

[Trait("Category", "SimulatedUnit")]
public sealed class FirmwareAndRomStudioTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FirmwareFormatRegistry _registry;
    private readonly RomStudioService _romStudio;

    public FirmwareAndRomStudioTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "transsionlab_fw_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var logger = new StructuredLogger(Path.Combine(_tempDir, "logs"));
        var profiles = new DeviceProfileRepository();
        _registry = new FirmwareFormatRegistry(profiles, logger);
        _romStudio = new RomStudioService(_registry, logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SparseImageInspector_ValidatesChunksAndComputesExactUnsparsedSize()
    {
        var path = Path.Combine(_tempDir, "super_test.img");
        await using (var fs = File.Create(path))
        {
            var hdr = new byte[28];
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0, 4), AndroidSparseAndBootParsers.SparseMagic);
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(4, 2), 1); // major
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(6, 2), 0); // minor
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(8, 2), 28); // file_hdr_sz
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(10, 2), 12); // chunk_hdr_sz
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12, 4), 4096); // blk_sz
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(16, 4), 10); // total_blks = 10 -> 40,960 B
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(20, 4), 2); // total_chunks = 2
            await fs.WriteAsync(hdr);

            // Chunk 1: RAW (2 blocks = 8192 B + 12 B hdr = 8204 B)
            var c1 = new byte[12];
            BinaryPrimitives.WriteUInt16LittleEndian(c1.AsSpan(0, 2), AndroidSparseAndBootParsers.ChunkTypeRaw);
            BinaryPrimitives.WriteUInt32LittleEndian(c1.AsSpan(4, 4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(c1.AsSpan(8, 4), 12 + 8192);
            await fs.WriteAsync(c1);
            await fs.WriteAsync(new byte[8192]);

            // Chunk 2: DONT_CARE (8 blocks = 12 B hdr)
            var c2 = new byte[12];
            BinaryPrimitives.WriteUInt16LittleEndian(c2.AsSpan(0, 2), AndroidSparseAndBootParsers.ChunkTypeDontCare);
            BinaryPrimitives.WriteUInt32LittleEndian(c2.AsSpan(4, 4), 8);
            BinaryPrimitives.WriteUInt32LittleEndian(c2.AsSpan(8, 4), 12);
            await fs.WriteAsync(c2);
        }

        var res = await _registry.InspectAsync(path);
        Assert.Equal(FirmwareFormatKind.AndroidSparseImage, res.PackageFormat);
        Assert.NotNull(res.SparseInfo);
        Assert.True(res.SparseInfo!.AccountingValid);
        Assert.Equal(40960L, res.SparseInfo.TotalUnsparsedBytes);
        Assert.Equal(1, res.SparseInfo.RawChunkCount);
        Assert.Equal(1, res.SparseInfo.DontCareChunkCount);
    }

    [Fact]
    public async Task BootImageV4Inspector_DecodesAndroid16HeaderAndPatchLevel()
    {
        var path = Path.Combine(_tempDir, "boot_a.img");
        var buf = new byte[4096];
        Encoding.ASCII.GetBytes("ANDROID!").CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), 33_554_432); // kernel size
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), 0); // GKI v4 boot has 0 ramdisk (in init_boot)
        // Encode OS 16.0.0 + patch 2026-10: ver=(16<<14)|(0<<7)|0; patch=((2026-2000)<<4)|10; raw=(ver<<11)|patch
        uint verPart = 16u << 14;
        uint patchPart = (26u << 4) | 10u;
        uint osRaw = (verPart << 11) | patchPart;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), osRaw);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20, 4), 1580);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40, 4), 4); // header_version = 4
        Encoding.ASCII.GetBytes("console=tty0 bootconfig").CopyTo(buf, 44);
        await File.WriteAllBytesAsync(path, buf);

        var res = await _registry.InspectAsync(path);
        Assert.Equal(FirmwareFormatKind.AndroidBootImage, res.PackageFormat);
        Assert.NotNull(res.BootInfo);
        Assert.Equal(4u, res.BootInfo!.HeaderVersion);
        Assert.Equal("16.0.0", res.BootInfo.DecodedOsVersion);
        Assert.Equal("2026-10", res.BootInfo.DecodedSecurityPatch);
        Assert.Equal("console=tty0 bootconfig", res.BootInfo.CommandLine);
    }

    [Fact]
    public async Task PayloadBinParser_DecodesCrAuV2ProtobufManifest()
    {
        var path = Path.Combine(_tempDir, "payload.bin");

        // Build minimal Protobuf DeltaArchiveManifest:
        // field 3 (block_size, varint) = 4096 -> tag (3<<3)|0 = 0x18, varint 4096 = 0x80 0x20
        // field 13 (PartitionUpdate, len-delimited) -> tag (13<<3)|2 = 0x6A
        //   inner: field 1 (partition_name="boot") -> 0x0A, 0x04, 'b','o','o','t'
        //   inner: field 7 (new_partition_info) -> 0x3A, len=4, [0x08, 0x80, 0x40] (size=8192) -> len=3
        byte[] partUpdate = [0x0A, 0x04, (byte)'b', (byte)'o', (byte)'o', (byte)'t', 0x3A, 0x03, 0x08, 0x80, 0x40, 0x42, 0x00];
        var manifest = new List<byte> { 0x18, 0x80, 0x20, 0x6A, (byte)partUpdate.Length };
        manifest.AddRange(partUpdate);

        await using (var fs = File.Create(path))
        {
            var hdr = new byte[24];
            Encoding.ASCII.GetBytes("CrAU").CopyTo(hdr, 0);
            BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(4, 8), 2); // version 2
            BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(12, 8), (ulong)manifest.Count);
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(20, 4), 0);
            await fs.WriteAsync(hdr);
            await fs.WriteAsync(manifest.ToArray());
        }

        var res = await _registry.InspectAsync(path);
        Assert.Equal(FirmwareFormatKind.AndroidPayloadBin, res.PackageFormat);
        Assert.NotNull(res.PayloadInfo);
        Assert.Equal(4096u, res.PayloadInfo!.BlockSize);
        Assert.Single(res.PayloadInfo.Partitions);
        Assert.Equal("boot", res.PayloadInfo.Partitions[0].PartitionName);
        Assert.Equal(8192UL, res.PayloadInfo.Partitions[0].NewPartitionBytes);
    }

    [Fact]
    public async Task UnisocPacBinaryContainer_InspectsAndExtractsPartitionPayload()
    {
        var pacPath = Path.Combine(_tempDir, "TECNO_BG6_T606.pac");
        byte[] bootPayload = Encoding.ASCII.GetBytes("ANDROID!SYNTHETIC_BOOT_PAYLOAD_DATA");
        int fileCount = 2; // FDL + BOOT
        int tableOffset = MediaTekAndUnisocParsers.PacHeaderSize;
        long bootDataOffset = tableOffset + fileCount * MediaTekAndUnisocParsers.PacFileEntrySize;

        await using (var fs = File.Create(pacPath))
        {
            var hdr = new byte[MediaTekAndUnisocParsers.PacHeaderSize];
            Encoding.Unicode.GetBytes("BP_R2.0.1").CopyTo(hdr, 0);
            Encoding.Unicode.GetBytes("TECNO-BG6").CopyTo(hdr, 56);
            Encoding.Unicode.GetBytes("V1.0").CopyTo(hdr, 568);
            BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(1080, 4), fileCount);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(1084, 4), (uint)tableOffset);
            await fs.WriteAsync(hdr);

            // Entry 0: FDL (0 bytes metadata for test -> triggers missing FDL2 dependency note)
            var e0 = new byte[MediaTekAndUnisocParsers.PacFileEntrySize];
            Encoding.Unicode.GetBytes("FDL").CopyTo(e0, 4);
            Encoding.Unicode.GetBytes("fdl1-sign.bin").CopyTo(e0, 516);
            await fs.WriteAsync(e0);

            // Entry 1: BOOT with real payload at bootDataOffset
            var e1 = new byte[MediaTekAndUnisocParsers.PacFileEntrySize];
            Encoding.Unicode.GetBytes("BOOT").CopyTo(e1, 4);
            Encoding.Unicode.GetBytes("boot.img").CopyTo(e1, 516);
            BinaryPrimitives.WriteUInt32LittleEndian(e1.AsSpan(1548, 4), (uint)bootPayload.Length);
            BinaryPrimitives.WriteInt32LittleEndian(e1.AsSpan(1552, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(e1.AsSpan(1560, 4), (uint)bootDataOffset);
            await fs.WriteAsync(e1);

            await fs.WriteAsync(bootPayload);
        }

        var inspection = await _registry.InspectAsync(pacPath);
        Assert.Equal(FirmwareFormatKind.UnisocPacContainer, inspection.PackageFormat);
        Assert.NotNull(inspection.PacInfo);
        Assert.Equal("TECNO-BG6", inspection.PacInfo!.ProductName);
        Assert.Contains("tecno-pop8-bg6", inspection.CompatibleProfileIds);

        var outDir = Path.Combine(_tempDir, "extracted_pac");
        var extractedBoot = await _registry.ExtractArchiveEntryAsync(pacPath, "BOOT", outDir);
        Assert.Equal(bootPayload, await File.ReadAllBytesAsync(extractedBoot));
    }

    [Fact]
    public async Task MediaTekScatterAndTarGzAndOtaZip_InspectsAccurately_AndSkipsProtectedNvramInRomStudio()
    {
        // 1. Scatter test
        var scatterDir = Path.Combine(_tempDir, "scatter_pkg");
        Directory.CreateDirectory(scatterDir);
        await File.WriteAllBytesAsync(Path.Combine(scatterDir, "boot.img"), new byte[1024]);
        var scatterPath = Path.Combine(scatterDir, "MT6789_Android_scatter.txt");
        await File.WriteAllTextAsync(scatterPath, """
            ############################################################################################################
            - general: MTK_PLATFORM_CFG
              info:
                - config_version: V1.1.2
                  platform: MT6789
                  project: TECNO-CL6
                  storage: UFS
            - partition_index: SYS0
              partition_name: boot_a
              file_name: boot.img
              is_download: true
              physical_start_addr: 0x8000000
              partition_size: 0x4000000
            - partition_index: SYS1
              partition_name: nvram
              file_name: nvram.bin
              is_download: true
              physical_start_addr: 0xC000000
              partition_size: 0x4000000
            """);

        var scatterRes = await _registry.InspectAsync(scatterPath);
        Assert.Equal(FirmwareFormatKind.MediaTekScatterText, scatterRes.PackageFormat);
        Assert.Equal("MT6789", scatterRes.ScatterInfo!.PlatformChipset);
        Assert.Equal(2, scatterRes.Partitions.Count);
        Assert.True(scatterRes.Partitions[1].IsProtectedNvOrRadio);
        Assert.False(scatterRes.Partitions[1].IsDownloadSelectedByDefault);

        // 2. Signed OTA ZIP + ROM Studio protected extraction skip test
        var zipPath = Path.Combine(_tempDir, "ota.zip");
        using (var zf = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var meta = zf.CreateEntry("META-INF/com/android/metadata");
            await using (var sw = new StreamWriter(meta.Open()))
            {
                await sw.WriteLineAsync("pre-device=TECNO-CL9");
                await sw.WriteLineAsync("post-build=TECNO/CL9-OP/TECNO-CL9:16/BP2A/1:user/release-keys");
            }

            var bootEntry = zf.CreateEntry("boot.img");
            await using (var s = bootEntry.Open()) await s.WriteAsync(new byte[] { 1, 2, 3, 4 });

            var nvEntry = zf.CreateEntry("nvram.img");
            await using (var s = nvEntry.Open()) await s.WriteAsync(new byte[] { 9, 9, 9 });
        }

        var zipRes = await _registry.InspectAsync(zipPath);
        Assert.Equal(FirmwareFormatKind.AndroidOtaZip, zipRes.PackageFormat);
        Assert.Contains("tecno-camon30-premier-cl9", zipRes.CompatibleProfileIds);

        var staging = Path.Combine(_tempDir, "staging");
        var extracted = await _romStudio.ExtractStockPartitionsForFastbootAsync(zipPath, ["boot.img", "nvram.img"], staging);
        Assert.Single(extracted); // nvram.img automatically skipped!

        // 3. ROM Studio Custom ROM validator never claims stock zip builds a custom ROM
        var envReport = _romStudio.ValidateCustomRomSourceWorkspace(_tempDir);
        Assert.False(envReport.ReadyForSourceBuild);
        Assert.NotEmpty(envReport.ValidationFindings);
    }

    [Fact]
    public async Task TarGzipInspector_ReadsUstarEntriesAndExtractsFile()
    {
        var tgzPath = Path.Combine(_tempDir, "images.tar.gz");
        byte[] vbmetaBytes = Encoding.ASCII.GetBytes("AVB0_TEST_VBMETA_HEADER_DATA");

        await using (var fs = File.Create(tgzPath))
        await using (var gz = new GZipStream(fs, CompressionMode.Compress))
        {
            var hdr = new byte[512];
            Encoding.ASCII.GetBytes("vbmeta.img").CopyTo(hdr, 0);
            var octalSize = Convert.ToString(vbmetaBytes.Length, 8).PadLeft(11, '0');
            Encoding.ASCII.GetBytes(octalSize).CopyTo(hdr, 124);
            hdr[156] = (byte)'0';
            Encoding.ASCII.GetBytes("ustar").CopyTo(hdr, 257);

            await gz.WriteAsync(hdr);
            var padded = new byte[512];
            vbmetaBytes.CopyTo(padded, 0);
            await gz.WriteAsync(padded);
            await gz.WriteAsync(new byte[1024]); // two zero EOF blocks
        }

        var res = await _registry.InspectAsync(tgzPath);
        Assert.Equal(FirmwareFormatKind.TarGzipArchive, res.PackageFormat);
        Assert.Single(res.ArchiveEntries);
        Assert.Equal("vbmeta.img", res.ArchiveEntries[0].RelativePath);

        var outPath = await _registry.ExtractArchiveEntryAsync(tgzPath, "vbmeta.img", Path.Combine(_tempDir, "tar_out"));
        Assert.Equal(vbmetaBytes, await File.ReadAllBytesAsync(outPath));
    }
}
