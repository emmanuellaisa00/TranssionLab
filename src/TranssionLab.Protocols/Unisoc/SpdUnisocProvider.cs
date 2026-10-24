using System.Text;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Protocols.Transport;
using TranssionLab.Protocols.Usb;

namespace TranssionLab.Protocols.Unisoc;

/// <summary>
/// Spreadtrum / Unisoc Download & Diagnostic protocol provider (Section 2C).
/// Implements documented HDLC 0x7E framing, byte-stuffing (0x7D escape), CRC-16 checksum,
/// and non-destructive BSL_CMD_CHECK_BAUD -> BSL_REP_VER (0x81) boot ROM version identification.
/// Explicitly separates Unisoc FDL1/FDL2 workflows from MediaTek DA workflows.
/// </summary>
public sealed class SpdUnisocProvider : ISpdUnisocProvider
{
    public const ushort BslRepVer = 0x0081;
    public const byte HdlcFlag = 0x7E;
    public const byte HdlcEscape = 0x7D;

    private readonly ISerialChannelFactory _serialFactory;
    private readonly IStructuredLogger _logger;

    public string ProviderName => "Unisoc / Spreadtrum Protocol";
    public ProviderImplementationStatus ImplementationStatus => ProviderImplementationStatus.ReadOnlyDiagnostics;

    public SpdUnisocProvider(ISerialChannelFactory serialFactory, IStructuredLogger logger)
    {
        _serialFactory = serialFactory;
        _logger = logger;
    }

    public IReadOnlyList<DetectedUsbDevice> FilterUnisocInterfaces(IReadOnlyList<DetectedUsbDevice> usbInventory) =>
        usbInventory
            .Where(u => u.VendorId == UsbSignatureCatalog.VidUnisoc ||
                        u.ClassifiedMode is ConnectionMode.UnisocDownload or ConnectionMode.UnisocDiag)
            .ToList();

    public async Task<UnisocBootProbeResult> ProbeReadOnlyDownloadVersionAsync(string comPort, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comPort);
        try
        {
            using var channel = _serialFactory.Create(comPort, 115200, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500));
            channel.Open();

            // Documented BSL_CMD_CHECK_BAUD is a single 0x7E byte to trigger BSL_REP_VER (0x0081) from Boot ROM
            await channel.WriteAsync(new byte[] { HdlcFlag }, ct).ConfigureAwait(false);

            var rxBuf = new byte[256];
            int total = 0;
            while (total < rxBuf.Length)
            {
                int n = await channel.ReadAsync(rxBuf.AsMemory(total, rxBuf.Length - total), ct).ConfigureAwait(false);
                if (n <= 0) break;
                total += n;
                if (total >= 8 && rxBuf[0] == HdlcFlag && rxBuf[total - 1] == HdlcFlag)
                    break;
            }

            if (total >= 8 && TryDecodeHdlcFrame(rxBuf.AsSpan(0, total), out var pktType, out var payload) && pktType == BslRepVer)
            {
                var banner = Encoding.ASCII.GetString(payload).TrimEnd('\0', '\r', '\n', ' ');
                var diag = $"Unisoc Boot ROM responded with BSL_REP_VER: '{banner}'. Secure Boot Unisoc devices (T603/T606/T612/T616) require OEM-signed FDL1 and FDL2 binaries inside a validated .pac package before RAM/flash download.";

                _logger.Log(new StructuredLogEntry
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    SessionId = _logger.CurrentSessionId,
                    SequenceNumber = 0,
                    Severity = LogSeverity.Info,
                    DeviceId = comPort,
                    ProtocolProvider = ProviderName,
                    UsbInterface = comPort,
                    OperationId = "unisoc.boot_version_probe",
                    RequestedAction = $"Unisoc HDLC 0x7E baud/version probe on {comPort}",
                    CommandOutput = $"BSL_REP_VER='{banner}'",
                    VerificationResult = diag
                });

                return new UnisocBootProbeResult(
                    PortOpened: true,
                    CheckBaudAckReceived: true,
                    BootRomBannerVersion: banner,
                    RequiresSignedFdl: true,
                    ResultingState: ConnectionDashboardState.AuthenticationRequired,
                    DiagnosticMessage: diag);
            }

            var noFrameMsg = $"Opened {comPort}, but did not receive a valid HDLC BSL_REP_VER (0x81) frame (read {total} bytes). Ensure device is in Unisoc Download Boot mode (1782:4D00).";
            _logger.LogWarning(ProviderName, $"Unisoc Probe ({comPort})", noFrameMsg, comPort, "unisoc.boot_version_probe");
            return new UnisocBootProbeResult(true, false, null, true, ConnectionDashboardState.DetectedButUnidentified, noFrameMsg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ProviderName, $"Unisoc Probe ({comPort})", ex.Message, null, comPort, "unisoc.boot_version_probe");
            return new UnisocBootProbeResult(false, false, null, true, ConnectionDashboardState.OperationFailed, ex.Message);
        }
    }

    public byte[] EncodeHdlcFrame(ushort packetType, ReadOnlySpan<byte> payload)
    {
        var len = (ushort)payload.Length;
        var raw = new byte[4 + payload.Length + 2];
        raw[0] = (byte)(packetType >> 8);
        raw[1] = (byte)(packetType & 0xFF);
        raw[2] = (byte)(len >> 8);
        raw[3] = (byte)(len & 0xFF);
        payload.CopyTo(raw.AsSpan(4));

        ushort crc = ComputeCrc16Xmodem(raw.AsSpan(0, 4 + payload.Length));
        raw[4 + payload.Length] = (byte)(crc >> 8);
        raw[4 + payload.Length + 1] = (byte)(crc & 0xFF);

        var encoded = new List<byte>(raw.Length + 8) { HdlcFlag };
        foreach (var b in raw)
        {
            if (b is HdlcFlag or HdlcEscape)
            {
                encoded.Add(HdlcEscape);
                encoded.Add((byte)(b ^ 0x20));
            }
            else
            {
                encoded.Add(b);
            }
        }
        encoded.Add(HdlcFlag);
        return encoded.ToArray();
    }

    public bool TryDecodeHdlcFrame(ReadOnlySpan<byte> frame, out ushort packetType, out byte[] payload)
    {
        packetType = 0;
        payload = Array.Empty<byte>();

        int start = frame.IndexOf(HdlcFlag);
        if (start < 0 || start + 1 >= frame.Length)
            return false;

        // Skip leading 0x7E sync bytes
        while (start + 1 < frame.Length && frame[start + 1] == HdlcFlag)
            start++;

        int end = frame[(start + 1)..].IndexOf(HdlcFlag);
        if (end < 0)
            return false;
        end += start + 1;

        var unescaped = new List<byte>(end - start);
        for (int i = start + 1; i < end; i++)
        {
            byte b = frame[i];
            if (b == HdlcEscape)
            {
                if (i + 1 >= end) return false;
                unescaped.Add((byte)(frame[++i] ^ 0x20));
            }
            else
            {
                unescaped.Add(b);
            }
        }

        if (unescaped.Count < 6)
            return false;

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(unescaped);
        packetType = (ushort)((span[0] << 8) | span[1]);
        ushort declaredLen = (ushort)((span[2] << 8) | span[3]);
        if (4 + declaredLen + 2 != span.Length)
            return false;

        ushort expectedCrc = (ushort)((span[^2] << 8) | span[^1]);
        ushort actualCrc = ComputeCrc16Xmodem(span[..^2]);
        if (expectedCrc != actualCrc)
            return false;

        payload = span.Slice(4, declaredLen).ToArray();
        return true;
    }

    public static ushort ComputeCrc16Xmodem(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }
}
