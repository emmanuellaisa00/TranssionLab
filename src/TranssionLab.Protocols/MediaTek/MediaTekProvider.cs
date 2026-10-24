using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Protocols.Transport;
using TranssionLab.Protocols.Usb;

namespace TranssionLab.Protocols.MediaTek;

/// <summary>
/// MediaTek Preloader / Boot ROM (BROM) / DA capability-aware provider (Section 2B).
/// Strictly separates USB interface detection from active protocol communication,
/// implements documented read-only identification & security-flag inspection (0xFD, 0xFC, 0xD8),
/// and reports SLA/DAA secure download requirements explicitly without implementing bypasses or exploits.
/// </summary>
public sealed class MediaTekProvider : IMediaTekProvider
{
    private readonly ISerialChannelFactory _serialFactory;
    private readonly IStructuredLogger _logger;

    private static readonly byte[] BromSyncTx = [0xA0, 0x0A, 0x50, 0x05];
    private static readonly byte[] BromSyncExpectedRx = [0x5F, 0xF5, 0xAF, 0xFA];

    public string ProviderName => "MediaTek BROM / Preloader / DA";
    public ProviderImplementationStatus ImplementationStatus => ProviderImplementationStatus.ReadOnlyDiagnostics;

    public MediaTekProvider(ISerialChannelFactory serialFactory, IStructuredLogger logger)
    {
        _serialFactory = serialFactory;
        _logger = logger;
    }

    public IReadOnlyList<DetectedUsbDevice> FilterMediaTekInterfaces(IReadOnlyList<DetectedUsbDevice> usbInventory) =>
        usbInventory
            .Where(u => u.VendorId == UsbSignatureCatalog.VidMediaTek ||
                        u.ClassifiedMode is ConnectionMode.MediaTekBrom
                            or ConnectionMode.MediaTekPreloader
                            or ConnectionMode.MediaTekDownloadAgent)
            .ToList();

    public async Task<MediaTekHandshakeProbeResult> ProbeReadOnlyBromOrPreloaderAsync(string comPort, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comPort);

        try
        {
            using var channel = _serialFactory.Create(comPort, 115200, TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(1200));
            channel.Open();

            // 1. Documented 4-byte BROM/Preloader handshake sync
            var txByte = new byte[1];
            var rxByte = new byte[1];
            for (int i = 0; i < BromSyncTx.Length; i++)
            {
                txByte[0] = BromSyncTx[i];
                await channel.WriteAsync(txByte, ct).ConfigureAwait(false);
                var read = await channel.ReadAsync(rxByte, ct).ConfigureAwait(false);
                if (read != 1 || rxByte[0] != BromSyncExpectedRx[i])
                {
                    var failMsg = $"Handshake sync byte {i} mismatch on {comPort} (expected 0x{BromSyncExpectedRx[i]:X2}, got 0x{(read == 1 ? rxByte[0] : 0):X2}). Device may have exited Preloader window.";
                    _logger.LogWarning(ProviderName, $"BROM Sync ({comPort})", failMsg, comPort, "mtk.brom_readonly_probe");
                    return new MediaTekHandshakeProbeResult(
                        PortOpened: true,
                        SyncSucceeded: false,
                        HardwareCode: null,
                        HwSubCode: null,
                        HwVersion: null,
                        SwVersion: null,
                        SecureBootEnabled: false,
                        SerialLinkAuthRequired: false,
                        DownloadAgentAuthRequired: false,
                        ChipsetFamilySummary: "Unidentified (Sync incomplete)",
                        ResultingState: ConnectionDashboardState.DetectedButUnidentified,
                        DiagnosticMessage: failMsg);
                }
            }

            // 2. Documented read-only GET_HW_CODE (0xFD): echoes 0xFD, returns big-endian u16 hw_code + u16 status
            ushort? hwCode = null;
            var cmd = new byte[] { 0xFD };
            await channel.WriteAsync(cmd, ct).ConfigureAwait(false);
            var hwBuf = await ReadExactOrPartialAsync(channel, 5, ct).ConfigureAwait(false);
            if (hwBuf.Length == 5 && hwBuf[0] == 0xFD)
            {
                hwCode = (ushort)((hwBuf[1] << 8) | hwBuf[2]);
            }

            // 3. Documented read-only GET_HW_SW_VER (0xFC): echoes 0xFC, returns hw_sub(2), hw_ver(2), sw_ver(2), status(2)
            ushort? hwSub = null, hwVer = null, swVer = null;
            await channel.WriteAsync(new byte[] { 0xFC }, ct).ConfigureAwait(false);
            var verBuf = await ReadExactOrPartialAsync(channel, 9, ct).ConfigureAwait(false);
            if (verBuf.Length == 9 && verBuf[0] == 0xFC)
            {
                hwSub = (ushort)((verBuf[1] << 8) | verBuf[2]);
                hwVer = (ushort)((verBuf[3] << 8) | verBuf[4]);
                swVer = (ushort)((verBuf[5] << 8) | verBuf[6]);
            }

            // 4. Documented read-only GET_TARGET_CONFIG (0xD8): echoes 0xD8, returns u32 configFlags + u16 status
            bool sbc = false, sla = false, daa = false;
            await channel.WriteAsync(new byte[] { 0xD8 }, ct).ConfigureAwait(false);
            var cfgBuf = await ReadExactOrPartialAsync(channel, 7, ct).ConfigureAwait(false);
            if (cfgBuf.Length == 7 && cfgBuf[0] == 0xD8)
            {
                uint flags = ((uint)cfgBuf[1] << 24) | ((uint)cfgBuf[2] << 16) | ((uint)cfgBuf[3] << 8) | cfgBuf[4];
                sbc = (flags & 0x01) != 0; // Secure Boot Enable
                sla = (flags & 0x02) != 0; // Serial Link Authentication
                daa = (flags & 0x04) != 0; // Download Agent Authentication
            }

            var chipsetSummary = hwCode.HasValue
                ? ResolveChipsetFamilyFromHwCode(hwCode.Value)
                : "Unknown MediaTek SoC";

            var authRestricted = sla || daa;
            var state = authRestricted
                ? ConnectionDashboardState.AuthenticationRequired
                : ConnectionDashboardState.ProtocolAvailable;

            var diag = authRestricted
                ? $"Read-only BROM probe succeeded ({chipsetSummary}, HW=0x{hwCode ?? 0:X4}). Hardware enforces SBC={sbc}, SLA={sla}, DAA={daa}. Manufacturer-signed Download Agent and auth certificate are required for storage/flashing; security bypasses are not implemented."
                : $"Read-only BROM probe succeeded ({chipsetSummary}, HW=0x{hwCode ?? 0:X4}). SBC={sbc}, SLA={sla}, DAA={daa}. Note: exact phone model is never inferred from SoC code alone.";

            _logger.Log(new StructuredLogEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                SessionId = _logger.CurrentSessionId,
                SequenceNumber = 0,
                Severity = LogSeverity.Info,
                DeviceId = comPort,
                ProtocolProvider = ProviderName,
                UsbInterface = comPort,
                OperationId = "mtk.brom_readonly_probe",
                RequestedAction = $"Read-only BROM handshake on {comPort}",
                CommandOutput = $"HW_CODE=0x{hwCode ?? 0:X4}, SBC={sbc}, SLA={sla}, DAA={daa}",
                VerificationResult = diag
            });

            return new MediaTekHandshakeProbeResult(
                PortOpened: true,
                SyncSucceeded: true,
                HardwareCode: hwCode,
                HwSubCode: hwSub,
                HwVersion: hwVer,
                SwVersion: swVer,
                SecureBootEnabled: sbc,
                SerialLinkAuthRequired: sla,
                DownloadAgentAuthRequired: daa,
                ChipsetFamilySummary: chipsetSummary,
                ResultingState: state,
                DiagnosticMessage: diag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ProviderName, $"BROM Probe ({comPort})", ex.Message, null, comPort, "mtk.brom_readonly_probe");
            return new MediaTekHandshakeProbeResult(
                false, false, null, null, null, null, false, false, false,
                "Probe failed",
                ConnectionDashboardState.OperationFailed,
                $"Unable to open or query {comPort}: {ex.Message}");
        }
    }

    private static async Task<byte[]> ReadExactOrPartialAsync(ISerialByteChannel channel, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = await channel.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (n <= 0) break;
            offset += n;
        }
        return offset == count ? buffer : buffer[..offset];
    }

    public string ResolveChipsetFamilyFromHwCode(ushort hwCode) => hwCode switch
    {
        0x0788 => "MediaTek MT6789 (Helio G99 / G99 Ultimate)",
        0x0707 => "MediaTek MT6768 / MT6769Z (Helio G85 / G88 / G91)",
        0x0766 => "MediaTek MT6765 (Helio G35 / G37 / P35)",
        0x0717 => "MediaTek MT6761 (Helio A22)",
        0x0959 => "MediaTek MT6833 / MT6833P (Dimensity 700 / 6020 / 6080)",
        0x0989 => "MediaTek MT6877 (Dimensity 900 / 1080 / 7050)",
        0x1129 => "MediaTek MT6895 / MT6896 (Dimensity 8100 / 8200 Ultimate)",
        0x1195 => "MediaTek MT6886 (Dimensity 7200)",
        0x0321 => "MediaTek MT6735 / MT6737",
        _ => $"MediaTek SoC (HW_CODE 0x{hwCode:X4})"
    };
}
