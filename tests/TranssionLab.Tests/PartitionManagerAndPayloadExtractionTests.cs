using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Services;
using TranssionLab.Firmware;
using TranssionLab.Logging;
using TranssionLab.Profiles;
using TranssionLab.Protocols.Android;
using TranssionLab.Protocols.Diagnostics;
using TranssionLab.Protocols.Discovery;
using TranssionLab.Protocols.MediaTek;
using TranssionLab.Protocols.Unisoc;
using TranssionLab.Protocols.Usb;
using TranssionLab.Servicing;
using TranssionLab.Tests.Mocks;
using TranssionLab.ViewModels;
using Xunit;

namespace TranssionLab.Tests;

[Trait("Category", "SimulatedUnit")]
public sealed class PartitionManagerAndPayloadExtractionTests : IDisposable
{
    private readonly string _tempDir;

    public PartitionManagerAndPayloadExtractionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "transsionlab_pm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task PartitionManager_ScansFastbootdPartitions_ClassifiesPolicies_AndManagesLogicalSuper()
    {
        long productSize = 2_147_483_648L;
        bool customCreated = false;

        var runner = new RecordedProcessRunner((exe, args) =>
        {
            if (exe == "adb")
                return new CommandExecutionResult(true, 0, "List of devices attached\n", "", 1);

            var cmd = string.Join(' ', args);
            if (cmd == "--version")
                return new CommandExecutionResult(true, 0, "fastboot version 35.0.2\n", "", 1);
            if (cmd == "devices -l")
                return new CommandExecutionResult(true, 0, "CL9FBD01\tfastbootd\n", "", 1);
            if (cmd.Contains("getvar is-userspace"))
                return new CommandExecutionResult(true, 0, "", "is-userspace: yes\n", 1);
            if (cmd.Contains("getvar unlocked"))
                return new CommandExecutionResult(true, 0, "", "unlocked: yes\n", 1);
            if (cmd.Contains("getvar all"))
            {
                var sb = new StringBuilder();
                sb.AppendLine("(bootloader) product: TECNO-CL9");
                sb.AppendLine("(bootloader) unlocked: yes");
                sb.AppendLine("(bootloader) is-userspace: yes");
                sb.AppendLine("(bootloader) current-slot: a");
                sb.AppendLine("(bootloader) super-partition-name: super");
                sb.AppendLine("(bootloader) partition-size:boot_a: 0x4000000");
                sb.AppendLine("(bootloader) is-logical:boot_a: no");
                sb.AppendLine($"(bootloader) partition-size:product_a: 0x{productSize:X}");
                sb.AppendLine("(bootloader) is-logical:product_a: yes");
                sb.AppendLine("(bootloader) partition-size:tr_mi_a: 0x10000000");
                sb.AppendLine("(bootloader) is-logical:tr_mi_a: yes");
                sb.AppendLine("(bootloader) partition-size:userdata: 0x100000000");
                sb.AppendLine("(bootloader) is-logical:userdata: no");
                sb.AppendLine("(bootloader) partition-size:metadata: 0x4000000");
                sb.AppendLine("(bootloader) is-logical:metadata: no");
                sb.AppendLine("(bootloader) partition-size:nvram: 0x4000000");
                sb.AppendLine("(bootloader) partition-size:frp: 0x100000");
                sb.AppendLine("(bootloader) partition-size:preloader: 0x80000");
                return new CommandExecutionResult(true, 0, "", sb.ToString(), 2);
            }
            if (cmd.Contains("create-logical-partition tr_custom_a"))
            {
                customCreated = true;
                return new CommandExecutionResult(true, 0, "", "Creating 'tr_custom_a' OKAY\n", 5);
            }
            if (cmd.Contains("getvar is-logical:tr_custom_a"))
            {
                return new CommandExecutionResult(true, 0, "", $"is-logical:tr_custom_a: {(customCreated ? "yes" : "no")}\n", 1);
            }
            if (cmd.Contains("resize-logical-partition product_a "))
            {
                productSize = long.Parse(args[^1]);
                return new CommandExecutionResult(true, 0, "", "Resizing 'product_a' OKAY\n", 5);
            }
            if (cmd.Contains("getvar partition-size:product_a"))
            {
                return new CommandExecutionResult(true, 0, "", $"partition-size:product_a: 0x{productSize:X}\n", 1);
            }
            if (cmd.Contains("delete-logical-partition tr_custom_a"))
            {
                customCreated = false;
                return new CommandExecutionResult(true, 0, "", "Deleting 'tr_custom_a' OKAY\n", 4);
            }
            if (cmd.Contains("erase metadata"))
            {
                return new CommandExecutionResult(true, 0, "", "Erasing 'metadata' OKAY\n", 4);
            }
            return new CommandExecutionResult(true, 0, "", "", 1);
        });

        var pm = BuildPartitionManagerHarness(runner, out var discovery);
        await discovery.RefreshInventoryAsync();
        var dev = discovery.RequireExplicitlySelectedDevice();

        var snap = await pm.ScanDevicePartitionsAsync(dev);
        Assert.Equal(8, snap.TotalPartitions);
        Assert.Equal(2, snap.LogicalPartitionsCount);
        Assert.Equal(3, snap.ProtectedPartitionsCount); // nvram + frp + preloader

        Assert.Equal(PartitionAccessPolicy.AntiTheftFrpBlocked, snap.Partitions.Single(p => p.PartitionName == "frp").AccessPolicy);
        Assert.Equal(PartitionAccessPolicy.ProtectedNvramOrCalibrationBlocked, snap.Partitions.Single(p => p.PartitionName == "nvram").AccessPolicy);
        Assert.Equal(PartitionAccessPolicy.BootChainCriticalBlocked, snap.Partitions.Single(p => p.PartitionName == "preloader").AccessPolicy);
        Assert.True(snap.Partitions.Single(p => p.PartitionName == "product_a").CanResizeOrDeleteLogical);

        // Create, Resize, and Delete logical partitions inside super
        var createRes = await pm.CreateLogicalPartitionAsync(dev, "tr_custom_a", 67_108_864, "SUPER CL9FBD01");
        Assert.True(createRes.Succeeded && createRes.PostVerified);

        var resizeRes = await pm.ResizeLogicalPartitionAsync(dev, "product_a", 2_684_354_560L, "SUPER CL9FBD01");
        Assert.True(resizeRes.Succeeded && resizeRes.PostVerified);

        var delRes = await pm.DeleteLogicalPartitionAsync(dev, "tr_custom_a", "SUPER CL9FBD01");
        Assert.True(delRes.Succeeded && delRes.PostVerified);

        // Erase guardrails: block FRP, NVRAM, preloader, and OS boot_a; allow confirmed metadata erase
        await Assert.ThrowsAsync<SafetyPolicyViolationException>(() => pm.ErasePartitionAsync(dev, "frp", "ERASE frp CL9FBD01"));
        await Assert.ThrowsAsync<SafetyPolicyViolationException>(() => pm.ErasePartitionAsync(dev, "nvram", "ERASE nvram CL9FBD01"));
        await Assert.ThrowsAsync<SafetyPolicyViolationException>(() => pm.ErasePartitionAsync(dev, "preloader", "ERASE preloader CL9FBD01"));
        await Assert.ThrowsAsync<SafetyPolicyViolationException>(() => pm.ErasePartitionAsync(dev, "boot_a", "ERASE boot_a CL9FBD01"));

        var eraseOk = await pm.ErasePartitionAsync(dev, "metadata", "ERASE metadata CL9FBD01");
        Assert.True(eraseOk.Succeeded && eraseOk.PostVerified);
    }

    [Fact]
    public async Task PartitionManager_ScansRecoveryAdbPartitions_ReadsBackBootImg_AndBlocksUserdataAndFrp()
    {
        var expectedBootPayload = Encoding.ASCII.GetBytes("ANDROID!MOCK_RECOVERY_READBACK_PAYLOAD_DATA_BLOCK");

        var runner = new RecordedProcessRunner((exe, args) =>
        {
            if (exe == "fastboot")
                return new CommandExecutionResult(true, 0, "", "", 1);

            var cmd = string.Join(' ', args);
            if (cmd == "devices -l")
                return new CommandExecutionResult(true, 0, "X6850REC\trecovery model:Infinix_X6850 device:Infinix-X6850\n", "", 1);
            if (cmd.Contains("id -u"))
                return new CommandExecutionResult(true, 0, "0\n", "", 1);
            if (cmd.Contains("/proc/partitions"))
                return new CommandExecutionResult(true, 0, "major minor #blocks name\n 259 14 65536 sdc14\n 253 0 4194304 dm-0\n", "", 1);
            if (cmd.Contains("/dev/block/by-name"))
            {
                var ls = """
                         lrwxrwxrwx 1 root root 16 2026-10-24 12:00 boot_a -> /dev/block/sdc14
                         lrwxrwxrwx 1 root root 15 2026-10-24 12:00 system_a -> /dev/block/dm-0
                         lrwxrwxrwx 1 root root 16 2026-10-24 12:00 userdata -> /dev/block/sdc42
                         lrwxrwxrwx 1 root root 15 2026-10-24 12:00 frp -> /dev/block/sdc9
                         """;
                return new CommandExecutionResult(true, 0, ls, "", 2);
            }
            if (args.Count >= 4 && args[2] == "pull" && args[3] == "/dev/block/sdc14")
            {
                File.WriteAllBytes(args[4], expectedBootPayload);
                return new CommandExecutionResult(true, 0, "1 file pulled", "", 5);
            }
            return new CommandExecutionResult(true, 0, "", "", 1);
        });

        var pm = BuildPartitionManagerHarness(runner, out var discovery);
        await discovery.RefreshInventoryAsync();
        var dev = discovery.RequireExplicitlySelectedDevice();

        var snap = await pm.ScanDevicePartitionsAsync(dev);
        Assert.Equal(4, snap.TotalPartitions);
        var bootEntry = snap.Partitions.Single(p => p.PartitionName == "boot_a");
        Assert.Equal(65536L * 1024L, bootEntry.SizeBytes);
        Assert.True(bootEntry.CanReadbackInCurrentMode);

        var userdataEntry = snap.Partitions.Single(p => p.PartitionName == "userdata");
        var frpEntry = snap.Partitions.Single(p => p.PartitionName == "frp");
        Assert.False(userdataEntry.CanReadbackInCurrentMode);
        Assert.False(frpEntry.CanReadbackInCurrentMode);

        var backupDir = Path.Combine(_tempDir, "backups");
        await Assert.ThrowsAsync<SafetyPolicyViolationException>(() => pm.BackupPartitionViaAdbAsync(dev, userdataEntry, backupDir));
        await Assert.ThrowsAsync<SafetyPolicyViolationException>(() => pm.BackupPartitionViaAdbAsync(dev, frpEntry, backupDir));

        var readback = await pm.BackupPartitionViaAdbAsync(dev, bootEntry, backupDir);
        Assert.True(readback.Succeeded);
        Assert.NotNull(readback.OutputFilePath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expectedBootPayload)).ToLowerInvariant(), readback.OutputSha256);
    }

    [Fact]
    public async Task PayloadBinExtractor_ExtractsFullPartitionFromPayloadAndOtaZip_VerifyingSha256()
    {
        // Construct a 2-block (8192-byte) partition: Block 0 = REPLACE (4096 bytes), Block 1 = ZERO (4096 bytes)
        var block0 = new byte[4096];
        Array.Fill(block0, (byte)0x5A);
        var fullExpectedImage = new byte[8192];
        Buffer.BlockCopy(block0, 0, fullExpectedImage, 0, 4096);
        var expectedShaBytes = SHA256.HashData(fullExpectedImage);
        var op0ShaBytes = SHA256.HashData(block0);

        byte[] payloadBin = BuildSyntheticCrAuV2Payload("init_boot", fullExpectedImage.Length, expectedShaBytes, block0, op0ShaBytes);
        var payloadPath = Path.Combine(_tempDir, "payload.bin");
        await File.WriteAllBytesAsync(payloadPath, payloadBin);

        // Also package payload.bin inside an Android OTA .zip
        var otaZipPath = Path.Combine(_tempDir, "ota_update.zip");
        using (var zf = ZipFile.Open(otaZipPath, ZipArchiveMode.Create))
        {
            var meta = zf.CreateEntry("META-INF/com/android/metadata");
            await using (var mw = new StreamWriter(meta.Open()))
            {
                await mw.WriteLineAsync("pre-device=TECNO-CL9");
            }
            var pe = zf.CreateEntry("payload.bin", CompressionLevel.NoCompression);
            await using (var ps = pe.Open())
            {
                await ps.WriteAsync(payloadBin);
            }
        }

        var registry = new FirmwareFormatRegistry(new DeviceProfileRepository(), new StructuredLogger(Path.Combine(_tempDir, "logs")));

        // 1. Extract from standalone payload.bin
        var outDir1 = Path.Combine(_tempDir, "out_payload");
        var extracted1 = await registry.ExtractArchiveEntryAsync(payloadPath, "init_boot", outDir1);
        Assert.Equal(fullExpectedImage, await File.ReadAllBytesAsync(extracted1));

        // 2. Extract directly from OTA .zip containing payload.bin
        var outDir2 = Path.Combine(_tempDir, "out_otazip");
        var extracted2 = await registry.ExtractArchiveEntryAsync(otaZipPath, "init_boot.img", outDir2);
        Assert.Equal(fullExpectedImage, await File.ReadAllBytesAsync(extracted2));
    }

    [Fact]
    public async Task SparseConverter_AndSuperLpMetadataParser_ExpandAndParseAccurately()
    {
        var registry = new FirmwareFormatRegistry(new DeviceProfileRepository(), new StructuredLogger(Path.Combine(_tempDir, "logs")));

        // 1. Build Sparse image: 1 RAW block (4096 B of 0xAB) + 1 FILL block (4096 B of 0xCD) + 1 DONT_CARE block (4096 B of 0x00) = 12288 B
        var sparsePath = Path.Combine(_tempDir, "test_sparse.img");
        WriteSyntheticSparseImage(sparsePath);

        var unsparsedPath = await registry.ConvertSparseImageToRawAsync(sparsePath, Path.Combine(_tempDir, "expanded.raw.img"));
        var expanded = await File.ReadAllBytesAsync(unsparsedPath);
        Assert.Equal(12288, expanded.Length);
        Assert.Equal(0xAB, expanded[0]);
        Assert.Equal(0xCD, expanded[4096]);
        Assert.Equal(0x00, expanded[8192]);

        // 2. Build raw super.img with LpMetadataGeometry @ 0x1000 and LpMetadataHeader @ 0x3000
        var superPath = Path.Combine(_tempDir, "super.img");
        WriteSyntheticSuperLpImage(superPath);

        var superInspect = await registry.InspectAsync(superPath);
        Assert.Equal(FirmwareFormatKind.AndroidSuperImage, superInspect.PackageFormat);
        Assert.NotNull(superInspect.SuperInfo);
        Assert.Equal(2, superInspect.SuperInfo!.LogicalPartitions.Count);
        Assert.Equal(4_294_967_296L, superInspect.SuperInfo.LogicalPartitions.Single(p => p.PartitionName == "system_a").SizeBytes);
        Assert.Equal(268_435_456L, superInspect.SuperInfo.LogicalPartitions.Single(p => p.PartitionName == "tr_mi_a").SizeBytes);
    }

    private static void WriteSyntheticSparseImage(string sparsePath)
    {
        using var sfs = File.Create(sparsePath);
        Span<byte> shdr = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(shdr[..4], 0xED26FF3A);
        BinaryPrimitives.WriteUInt16LittleEndian(shdr.Slice(4, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(shdr.Slice(6, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(shdr.Slice(8, 2), 28);
        BinaryPrimitives.WriteUInt16LittleEndian(shdr.Slice(10, 2), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(shdr.Slice(12, 4), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(shdr.Slice(16, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(shdr.Slice(20, 4), 3);
        sfs.Write(shdr);

        Span<byte> chdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(chdr[..2], 0xCAC1);
        BinaryPrimitives.WriteUInt32LittleEndian(chdr.Slice(4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(chdr.Slice(8, 4), 12 + 4096);
        sfs.Write(chdr);
        var rawBlk = new byte[4096];
        Array.Fill(rawBlk, (byte)0xAB);
        sfs.Write(rawBlk);

        BinaryPrimitives.WriteUInt16LittleEndian(chdr[..2], 0xCAC2);
        BinaryPrimitives.WriteUInt32LittleEndian(chdr.Slice(4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(chdr.Slice(8, 4), 16);
        sfs.Write(chdr);
        sfs.Write([0xCD, 0xCD, 0xCD, 0xCD]);

        BinaryPrimitives.WriteUInt16LittleEndian(chdr[..2], 0xCAC3);
        BinaryPrimitives.WriteUInt32LittleEndian(chdr.Slice(4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(chdr.Slice(8, 4), 12);
        sfs.Write(chdr);
    }

    private static void WriteSyntheticSuperLpImage(string superPath)
    {
        var superBuf = new byte[0x5000];
        BinaryPrimitives.WriteUInt32LittleEndian(superBuf.AsSpan(0x1000, 4), 0x616C4467);
        BinaryPrimitives.WriteUInt32LittleEndian(superBuf.AsSpan(0x1000 + 40, 4), 65536);
        BinaryPrimitives.WriteUInt32LittleEndian(superBuf.AsSpan(0x1000 + 44, 4), 2);

        int h = 0x3000;
        BinaryPrimitives.WriteUInt32LittleEndian(superBuf.AsSpan(h, 4), 0x414C5030);
        BinaryPrimitives.WriteUInt16LittleEndian(superBuf.AsSpan(h + 4, 2), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(superBuf.AsSpan(h + 6, 2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(superBuf.AsSpan(h + 8, 4), 128);

        WriteTableDesc(superBuf.AsSpan(h + 76, 12), 0, 2, 52);
        WriteTableDesc(superBuf.AsSpan(h + 88, 12), 104, 2, 24);
        WriteTableDesc(superBuf.AsSpan(h + 100, 12), 152, 1, 48);

        int tb = h + 128;
        WriteLpPartition(superBuf.AsSpan(tb, 52), "system_a", firstExtent: 0, numExtents: 1, groupIdx: 0);
        WriteLpPartition(superBuf.AsSpan(tb + 52, 52), "tr_mi_a", firstExtent: 1, numExtents: 1, groupIdx: 0);
        BinaryPrimitives.WriteUInt64LittleEndian(superBuf.AsSpan(tb + 104, 8), 8_388_608UL);
        BinaryPrimitives.WriteUInt64LittleEndian(superBuf.AsSpan(tb + 128, 8), 524_288UL);
        Encoding.ASCII.GetBytes("transsion_dynamic_partitions_a").CopyTo(superBuf.AsSpan(tb + 152, 36));
        File.WriteAllBytes(superPath, superBuf);
    }

    [Fact]
    public void ServiceRegistration_ResolvesAll19PagesAndPartitionManager()
    {
        var services = new ServiceCollection();
        services.AddTranssionLabServices(
            customDataDirectory: _tempDir,
            customPnpBackend: new SimulatedPnpBackend(),
            customProcessRunner: new RecordedProcessRunner((_, _) => new CommandExecutionResult(true, 0, "", "", 1)),
            customSerialFactory: new SimulatedSerialChannelFactory((_, _) => []));

        using var provider = services.BuildServiceProvider();
        var shell = provider.GetRequiredService<MainShellViewModel>();
        Assert.Equal(19, shell.Pages.Count);
        Assert.Contains(shell.Pages, p => p.PageTitle == "Partition Manager" && p.PageNumber == 13);
    }

    private PartitionManager BuildPartitionManagerHarness(RecordedProcessRunner runner, out DeviceDiscoveryProvider discovery)
    {
        var settings = new AppSettingsService(_tempDir);
        var logger = new StructuredLogger(Path.Combine(_tempDir, "logs"));
        var history = new OperationHistoryStore(Path.Combine(_tempDir, "history"));
        var repo = new DeviceProfileRepository(Path.Combine(_tempDir, "profiles"));
        var usb = new UsbInterfaceProvider(new SimulatedPnpBackend(), logger);
        var adb = new AdbProvider(runner, settings, logger);
        var rec = new RecoveryProvider(adb);
        var fb = new FastbootProvider(runner, settings, logger);
        var serial = new SimulatedSerialChannelFactory((_, _) => []);
        var cap = new ProtocolCapabilityService(usb, adb, rec, fb, new MediaTekProvider(serial, logger), new MetaModeProvider(settings, logger), new SpdUnisocProvider(serial, logger), new DiagnosticProvider(serial, logger), new OperationRegistry());
        discovery = new DeviceDiscoveryProvider(usb, adb, fb, repo, cap, logger);
        var fw = new FirmwareFormatRegistry(repo, logger);
        return new PartitionManager(discovery, repo, fb, adb, fw, logger, history);
    }

    private static void WriteTableDesc(Span<byte> dst, uint offset, uint count, uint entrySize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst[..4], offset);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4, 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(8, 4), entrySize);
    }

    private static void WriteLpPartition(Span<byte> dst, string name, uint firstExtent, uint numExtents, uint groupIdx)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(dst[..36]);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(40, 4), firstExtent);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(44, 4), numExtents);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(48, 4), groupIdx);
    }

    private static byte[] BuildSyntheticCrAuV2Payload(
        string partitionName,
        int totalSize,
        byte[] expectedPartSha256,
        byte[] op0RawData,
        byte[] op0DataSha256)
    {
        // Build Protobuf wire bytes for PartitionUpdate:
        // Extent 0: start_block=0 (field 1=0), num_blocks=1 (field 2=1) -> [0x08, 0x00, 0x10, 0x01]
        byte[] ext0 = [0x08, 0x00, 0x10, 0x01];
        // Extent 1: start_block=1 (field 1=1), num_blocks=1 (field 2=1) -> [0x08, 0x01, 0x10, 0x01]
        byte[] ext1 = [0x08, 0x01, 0x10, 0x01];

        // InstallOperation 0 (REPLACE=0, data_offset=0, data_length=4096, dst_extents=ext0, data_sha256=op0DataSha256)
        using var op0Ms = new MemoryStream();
        WriteProtoVarintField(op0Ms, 1, 0); // REPLACE
        WriteProtoVarintField(op0Ms, 2, 0); // data_offset = 0
        WriteProtoVarintField(op0Ms, 3, (ulong)op0RawData.Length); // data_length
        WriteProtoBytesField(op0Ms, 5, ext0);
        WriteProtoBytesField(op0Ms, 6, op0DataSha256);

        // InstallOperation 1 (ZERO=6, dst_extents=ext1)
        using var op1Ms = new MemoryStream();
        WriteProtoVarintField(op1Ms, 1, 6); // ZERO
        WriteProtoBytesField(op1Ms, 5, ext1);

        // PartitionInfo (field 1=size, field 2=sha256)
        using var pInfoMs = new MemoryStream();
        WriteProtoVarintField(pInfoMs, 1, (ulong)totalSize);
        WriteProtoBytesField(pInfoMs, 2, expectedPartSha256);

        // PartitionUpdate (field 1=name, field 7=new_partition_info, field 8=operations)
        using var pUpdMs = new MemoryStream();
        WriteProtoBytesField(pUpdMs, 1, Encoding.UTF8.GetBytes(partitionName));
        WriteProtoBytesField(pUpdMs, 7, pInfoMs.ToArray());
        WriteProtoBytesField(pUpdMs, 8, op0Ms.ToArray());
        WriteProtoBytesField(pUpdMs, 8, op1Ms.ToArray());

        // DeltaArchiveManifest (field 3=block_size 4096, field 12=minor_version 0, field 13=partitions)
        using var manMs = new MemoryStream();
        WriteProtoVarintField(manMs, 3, 4096);
        WriteProtoVarintField(manMs, 12, 0);
        WriteProtoBytesField(manMs, 13, pUpdMs.ToArray());
        var manifestBytes = manMs.ToArray();

        using var fullOut = new MemoryStream();
        Span<byte> hdr = stackalloc byte[24];
        Encoding.ASCII.GetBytes("CrAU").CopyTo(hdr[..4]);
        BinaryPrimitives.WriteUInt64BigEndian(hdr.Slice(4, 8), 2UL);
        BinaryPrimitives.WriteUInt64BigEndian(hdr.Slice(12, 8), (ulong)manifestBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.Slice(20, 4), 0U);
        fullOut.Write(hdr);
        fullOut.Write(manifestBytes);
        fullOut.Write(op0RawData);
        return fullOut.ToArray();
    }

    private static void WriteProtoVarintField(Stream s, int fieldNumber, ulong value)
    {
        WriteRawVarint(s, (ulong)(fieldNumber << 3));
        WriteRawVarint(s, value);
    }

    private static void WriteProtoBytesField(Stream s, int fieldNumber, byte[] payload)
    {
        WriteRawVarint(s, (ulong)((fieldNumber << 3) | 2));
        WriteRawVarint(s, (ulong)payload.Length);
        s.Write(payload, 0, payload.Length);
    }

    private static void WriteRawVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }
}
