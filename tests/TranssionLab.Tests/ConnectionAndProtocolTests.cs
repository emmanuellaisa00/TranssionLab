using System.Text;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Services;
using TranssionLab.Logging;
using TranssionLab.Profiles;
using TranssionLab.Protocols.Android;
using TranssionLab.Protocols.Diagnostics;
using TranssionLab.Protocols.Discovery;
using TranssionLab.Protocols.MediaTek;
using TranssionLab.Protocols.Transport;
using TranssionLab.Protocols.Unisoc;
using TranssionLab.Protocols.Usb;
using TranssionLab.Tests.Mocks;
using Xunit;

namespace TranssionLab.Tests;

[Trait("Category", "SimulatedUnit")]
public sealed class ConnectionAndProtocolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettingsService _settings;
    private readonly StructuredLogger _logger;

    public ConnectionAndProtocolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "transsionlab_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settings = new AppSettingsService(_tempDir);
        _logger = new StructuredLogger(Path.Combine(_tempDir, "logs"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void AdbParser_DistinguishesConnectedUnauthorizedOfflineRecoveryAndSideload()
    {
        const string sample = """
            List of devices attached
            0987654321ABCDEF       device usb:1-2 product:CL9-OP model:TECNO_CL9 device:TECNO-CL9 transport_id:1
            UNAUTH123456           unauthorized usb:1-3 transport_id:2
            OFFLINE999             offline transport_id:3
            REC55555               recovery product:X6850 model:Infinix_X6850 device:Infinix-X6850
            SIDE77777              sideload transport_id:5
            """;

        var parsed = AdbProvider.ParseAdbDevicesOutput(sample);
        Assert.Equal(5, parsed.Count);
        Assert.Equal(AdbDeviceState.Connected, parsed[0].State);
        Assert.Equal("TECNO CL9", parsed[0].ModelHint);
        Assert.Equal(AdbDeviceState.Unauthorized, parsed[1].State);
        Assert.Equal(AdbDeviceState.Offline, parsed[2].State);
        Assert.Equal(AdbDeviceState.Recovery, parsed[3].State);
        Assert.Equal(AdbDeviceState.Sideload, parsed[4].State);
    }

    [Fact]
    public async Task FastbootProvider_DistinguishesBootloaderFromUserspaceFastbootd()
    {
        var runner = new RecordedProcessRunner((_, args) =>
        {
            var joined = string.Join(' ', args);
            if (joined == "devices -l")
            {
                return new CommandExecutionResult(true, 0, "CL9BOOT01\tfastboot\nX6871USR02\tfastboot\n", "", 5);
            }
            if (joined == "-s CL9BOOT01 getvar is-userspace")
            {
                return new CommandExecutionResult(true, 0, "", "is-userspace: no\nFinished. Total time: 0.002s", 3);
            }
            if (joined == "-s X6871USR02 getvar is-userspace")
            {
                return new CommandExecutionResult(true, 0, "", "(bootloader) is-userspace: yes\nFinished. Total time: 0.002s", 3);
            }
            return new CommandExecutionResult(true, 0, "", "", 1);
        });

        var fb = new FastbootProvider(runner, _settings, _logger);
        var list = await fb.ListDevicesAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal(ConnectionMode.FastbootBootloader, list[0].Mode);
        Assert.Equal(ConnectionMode.FastbootdUserspace, list[1].Mode);
    }

    [Fact]
    public async Task MultiDeviceDiscovery_NeverSilentlySelectsDevice_RequiresExplicitSelection()
    {
        var pnp = new SimulatedPnpBackend();
        var runner = new RecordedProcessRunner((exe, args) =>
        {
            var cmd = string.Join(' ', args);
            if (exe == "adb" && cmd == "devices -l")
            {
                return new CommandExecutionResult(true, 0,
                    "SERIAL_CL9 device model:TECNO_CL9 device:TECNO-CL9\nSERIAL_X6850 unauthorized\n", "", 5);
            }
            if (exe == "adb" && cmd == "-s SERIAL_CL9 shell getprop")
            {
                return new CommandExecutionResult(true, 0,
                    "[ro.product.manufacturer]: [TECNO]\n[ro.product.model]: [TECNO CL9]\n[ro.product.device]: [TECNO-CL9]\n[ro.build.version.release]: [16]\n[ro.build.fingerprint]: [TECNO/CL9-OP/TECNO-CL9:16/BP2A.250605.031/261001:user/release-keys]\n[ro.boot.flash.locked]: [1]\n",
                    "", 8);
            }
            return new CommandExecutionResult(true, 0, "", "", 2);
        });

        var usb = new UsbInterfaceProvider(pnp, _logger);
        var adb = new AdbProvider(runner, _settings, _logger);
        var rec = new RecoveryProvider(adb);
        var fb = new FastbootProvider(runner, _settings, _logger);
        var serialFactory = new SimulatedSerialChannelFactory((_, _) => []);
        var mtk = new MediaTekProvider(serialFactory, _logger);
        var meta = new MetaModeProvider(_settings, _logger);
        var unisoc = new SpdUnisocProvider(serialFactory, _logger);
        var diag = new DiagnosticProvider(serialFactory, _logger);
        var profiles = new DeviceProfileRepository();
        var ops = new OperationRegistry();
        var capSvc = new ProtocolCapabilityService(usb, adb, rec, fb, mtk, meta, unisoc, diag, ops);
        var discovery = new DeviceDiscoveryProvider(usb, adb, fb, profiles, capSvc, _logger);

        var devices = await discovery.RefreshInventoryAsync();
        Assert.Equal(2, devices.Count);

        // Hard Rule: With 2 devices connected, SelectedDevice MUST be null and RequireExplicitlySelectedDevice throws!
        Assert.True(discovery.RequiresExplicitSelection);
        Assert.Null(discovery.SelectedDevice);
        Assert.Throws<AmbiguousDeviceSelectionException>(() => discovery.RequireExplicitlySelectedDevice());

        // Explicit selection resolves cleanly
        discovery.SelectDeviceExplicitly("ADB:SERIAL_CL9");
        Assert.False(discovery.RequiresExplicitSelection);
        var selected = discovery.RequireExplicitlySelectedDevice();
        Assert.Equal("tecno-camon30-premier-cl9", selected.MatchedProfileId);
        Assert.Equal(DataProvenance.Verified, selected.ExactModel.Provenance);
    }

    [Fact]
    public async Task MediaTekBromDetection_NeverInfersExactModelFromChipset_AndReportsSlaDaaAuthRequirement()
    {
        var pnp = new SimulatedPnpBackend
        {
            Devices =
            [
                new RawPnpDeviceRecord(
                    @"USB\VID_0E8D&PID_0003\5&1A2B3C&0&1",
                    "MediaTek USB Port (COM7)",
                    "MediaTek Inc.",
                    "usbser",
                    0,
                    "OK")
            ]
        };

        // Simulate documented BROM sync + GET_HW_CODE (0x0788 = MT6789 Helio G99) + GET_TARGET_CONFIG (SBC|SLA|DAA = 0x07)
        int syncIdx = 0;
        byte[] syncReplies = [0x5F, 0xF5, 0xAF, 0xFA];
        var serialFactory = new SimulatedSerialChannelFactory((_, tx) =>
        {
            if (tx.Length == 1 && syncIdx < 4)
            {
                return [syncReplies[syncIdx++]];
            }
            return tx[0] switch
            {
                0xFD => [0xFD, 0x07, 0x88, 0x00, 0x00], // HW_CODE 0x0788 (MT6789)
                0xFC => [0xFC, 0x8A, 0x00, 0xCA, 0x00, 0x00, 0x00, 0x00, 0x00],
                0xD8 => [0xD8, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00], // SBC=1, SLA=1, DAA=1
                _ => []
            };
        });

        var runner = new RecordedProcessRunner((_, _) => new CommandExecutionResult(false, -1, "", "", 1));
        var usb = new UsbInterfaceProvider(pnp, _logger);
        var adb = new AdbProvider(runner, _settings, _logger);
        var rec = new RecoveryProvider(adb);
        var fb = new FastbootProvider(runner, _settings, _logger);
        var mtk = new MediaTekProvider(serialFactory, _logger);
        var meta = new MetaModeProvider(_settings, _logger);
        var unisoc = new SpdUnisocProvider(serialFactory, _logger);
        var diag = new DiagnosticProvider(serialFactory, _logger);
        var profiles = new DeviceProfileRepository();
        var capSvc = new ProtocolCapabilityService(usb, adb, rec, fb, mtk, meta, unisoc, diag, new OperationRegistry());
        var discovery = new DeviceDiscoveryProvider(usb, adb, fb, profiles, capSvc, _logger);

        var snapshots = await discovery.RefreshInventoryAsync();
        Assert.Single(snapshots);
        var snap = snapshots[0];

        // Exact model MUST remain Unknown in BROM mode — never guessed from chipset!
        Assert.Equal(DataProvenance.Unknown, snap.ExactModel.Provenance);
        Assert.Equal(ConnectionDashboardState.DetectedButUnidentified, snap.DashboardState);

        // Run read-only BROM probe
        var probe = await mtk.ProbeReadOnlyBromOrPreloaderAsync("COM7");
        Assert.True(probe.SyncSucceeded);
        Assert.Equal((ushort)0x0788, probe.HardwareCode);
        Assert.Contains("MT6789", probe.ChipsetFamilySummary);
        Assert.True(probe.SecureBootEnabled);
        Assert.True(probe.SerialLinkAuthRequired);
        Assert.True(probe.DownloadAgentAuthRequired);
        Assert.Equal(ConnectionDashboardState.AuthenticationRequired, probe.ResultingState);
    }

    [Fact]
    public async Task MetaMode_DoesNotFabricateConnectivityForGenericUsbOrMissingProvider()
    {
        var meta = new MetaModeProvider(_settings, _logger);
        Assert.False(meta.IsAuthorizedMetaLibraryInstalled(out _));
        Assert.Equal(ProviderImplementationStatus.DetectionOnly, meta.ImplementationStatus);

        var pnp = new SimulatedPnpBackend
        {
            Devices =
            [
                new RawPnpDeviceRecord(@"USB\VID_2ECC&PID_2008\SN123", "TECNO MTP USB", "Transsion", "WUDFRd", 0, "OK"),
                new RawPnpDeviceRecord(@"USB\VID_0E8D&PID_2007&MI_00\SN456", "MediaTek ETS META (COM9)", "MediaTek", "usbser", 0, "OK")
            ]
        };
        var usb = new UsbInterfaceProvider(pnp, _logger);
        var list = await usb.EnumerateUsbDevicesAsync();

        var detections = meta.DetectMetaInterfaces(list);
        Assert.Single(detections); // Generic MTP ignored!
        Assert.False(detections[0].ExternalCommunicationProviderAvailable);

        var diagRes = await meta.DiagnoseMetaConnectionAsync(detections[0].UsbDevice);
        Assert.True(diagRes.UsbEndpointVerified);
        Assert.False(diagRes.SessionEstablished);
        Assert.Equal(ConnectionDashboardState.ProtocolUnsupported, diagRes.State);
    }

    [Fact]
    public async Task UnisocHdlcFraming_RoundTripsEscapedPayload_AndProbesBootRomBanner()
    {
        SpdUnisocProvider? unisocRef = null;
        var serialFactory = new SimulatedSerialChannelFactory((_, tx) =>
        {
            if (tx.Length == 1 && tx[0] == 0x7E && unisocRef is not null)
            {
                return unisocRef.EncodeHdlcFrame(SpdUnisocProvider.BslRepVer, Encoding.ASCII.GetBytes("SPRD3\0"));
            }
            return [];
        });

        var unisoc = new SpdUnisocProvider(serialFactory, _logger);
        unisocRef = unisoc;

        // Test payload containing 0x7E and 0x7D escape-requiring bytes
        byte[] trickyPayload = [0x01, 0x7E, 0x7D, 0xFF, 0x00];
        var frame = unisoc.EncodeHdlcFrame(0x0080, trickyPayload);
        Assert.True(unisoc.TryDecodeHdlcFrame(frame, out var decodedType, out var decodedPayload));
        Assert.Equal((ushort)0x0080, decodedType);
        Assert.Equal(trickyPayload, decodedPayload);

        // Corrupt a byte -> CRC-16 XMODEM must reject
        frame[4] ^= 0xFF;
        Assert.False(unisoc.TryDecodeHdlcFrame(frame, out _, out _));

        // Probe read-only BSL_REP_VER banner
        var probe = await unisoc.ProbeReadOnlyDownloadVersionAsync("COM12");
        Assert.True(probe.CheckBaudAckReceived);
        Assert.Equal("SPRD3", probe.BootRomBannerVersion);
        Assert.Equal(ConnectionDashboardState.AuthenticationRequired, probe.ResultingState);
    }

    [Fact]
    public async Task DiagnosticProvider_BlocksQualcommQcdmCommandsOnUnisocAndMediaTekDevices()
    {
        var pnp = new SimulatedPnpBackend
        {
            Devices =
            [
                new RawPnpDeviceRecord(@"USB\VID_1782&PID_4D10&MI_01\6&999", "SPRD U2S Diag (COM14)", "Unisoc", "SciU2S", 0, "OK")
            ]
        };
        var usb = new UsbInterfaceProvider(pnp, _logger);
        var diag = new DiagnosticProvider(new SimulatedSerialChannelFactory((_, _) => []), _logger);

        var usbDevs = await usb.EnumerateUsbDevicesAsync();
        var classified = diag.ClassifyDiagnosticInterfaces(usbDevs);
        Assert.Single(classified);
        Assert.Equal(ChipsetVendor.Unisoc, classified[0].VendorFamily);
        Assert.False(classified[0].SupportsReadOnlyVersionProbe);

        var query = await diag.QueryReadOnlyDiagnosticVersionAsync(classified[0]);
        Assert.False(query.Executed);
        Assert.Contains("Refused to send Qualcomm QCDM frame", query.Summary);
    }

    [Theory]
    [InlineData(28, DriverHealthStatus.MissingDriverCode28)]
    [InlineData(10, DriverHealthStatus.CannotStartCode10)]
    [InlineData(43, DriverHealthStatus.DescriptorFailureCode43)]
    [InlineData(52, DriverHealthStatus.UnsignedBlockedCode52)]
    public void UsbDriverDiagnostics_MapsWindowsErrorCodesAndNeverAdvisesDisablingSignatureEnforcement(
        int errorCode,
        DriverHealthStatus expectedStatus)
    {
        var diag = UsbSignatureCatalog.EvaluateDriverHealth(errorCode, null, "Unknown", ConnectionMode.FastbootBootloader);
        Assert.Equal(expectedStatus, diag.Status);
        Assert.DoesNotContain("testsigning", diag.SafeRemediationGuidance, StringComparison.OrdinalIgnoreCase);
    }
}
