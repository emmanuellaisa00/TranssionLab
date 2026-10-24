using System.Text;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Protocols.Transport;

namespace TranssionLab.Protocols.Diagnostics;

/// <summary>
/// Dedicated DIAG interface classifier & read-only query provider (Section 2E).
/// Never assumes MediaTek or Unisoc Transsion devices support Qualcomm QCDM,
/// and blocks all NV/radio write commands.
/// </summary>
public sealed class DiagnosticProvider : IDiagnosticProvider
{
    private readonly ISerialChannelFactory _serialFactory;
    private readonly IStructuredLogger _logger;

    public string ProviderName => "DIAG & Radio Diagnostics";
    public ProviderImplementationStatus ImplementationStatus => ProviderImplementationStatus.ReadOnlyDiagnostics;

    public DiagnosticProvider(ISerialChannelFactory serialFactory, IStructuredLogger logger)
    {
        _serialFactory = serialFactory;
        _logger = logger;
    }

    public IReadOnlyList<DiagInterfaceAssessment> ClassifyDiagnosticInterfaces(IReadOnlyList<DetectedUsbDevice> usbInventory)
    {
        var list = new List<DiagInterfaceAssessment>();
        foreach (var usb in usbInventory)
        {
            switch (usb.ClassifiedMode)
            {
                case ConnectionMode.QualcommDiag:
                    list.Add(new DiagInterfaceAssessment(
                        usb,
                        ChipsetVendor.Qualcomm,
                        "Qualcomm QCDM / DIAG",
                        SupportsReadOnlyVersionProbe: !string.IsNullOrWhiteSpace(usb.AssociatedComPort),
                        GuardrailNotice: "Confirmed Qualcomm DIAG VID 0x05C6. Only documented read-only DIAG_VERNO_F (0x00) version query is permitted; NV writes are blocked."));
                    break;

                case ConnectionMode.UnisocDiag:
                    list.Add(new DiagInterfaceAssessment(
                        usb,
                        ChipsetVendor.Unisoc,
                        "Unisoc / Spreadtrum DIAG",
                        SupportsReadOnlyVersionProbe: false,
                        GuardrailNotice: "Unisoc diagnostic endpoint (VID 0x1782). Qualcomm QCDM commands are incompatible and blocked; protected prodnv/calibration writes are prohibited."));
                    break;

                case ConnectionMode.VendorDiag:
                    list.Add(new DiagInterfaceAssessment(
                        usb,
                        usb.DetectedChipsetVendor,
                        "MediaTek ELT / Vendor Diagnostic",
                        SupportsReadOnlyVersionProbe: false,
                        GuardrailNotice: "MediaTek ELT/Vendor diagnostic endpoint. Requires OEM ELT trace parser; Qualcomm DIAG commands are not sent to MediaTek chipsets."));
                    break;
            }
        }
        return list;
    }

    public async Task<DiagReadOnlyQueryResult> QueryReadOnlyDiagnosticVersionAsync(
        DiagInterfaceAssessment endpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.VendorFamily != ChipsetVendor.Qualcomm || endpoint.UsbDevice.ClassifiedMode != ConnectionMode.QualcommDiag)
        {
            var blockedMsg = $"Refused to send Qualcomm QCDM frame to {endpoint.ProtocolFamily} ({endpoint.VendorFamily}). Chipset-specific DIAG guardrails enforced.";
            _logger.LogWarning(ProviderName, "QueryReadOnlyDiagnosticVersion", blockedMsg, endpoint.UsbDevice.InstanceId, "diag.readonly_version_query");
            return new DiagReadOnlyQueryResult(false, false, endpoint.ProtocolFamily, null, null, blockedMsg);
        }

        if (string.IsNullOrWhiteSpace(endpoint.UsbDevice.AssociatedComPort))
        {
            return new DiagReadOnlyQueryResult(false, false, endpoint.ProtocolFamily, null, null, "No Windows COM port is mapped to this Qualcomm DIAG interface.");
        }

        var port = endpoint.UsbDevice.AssociatedComPort!;
        try
        {
            using var channel = _serialFactory.Create(port, 115200, TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(1200));
            channel.Open();

            // Documented read-only DIAG_VERNO_F = 0x00 + CRC-16/CCITT-FALSE (LSB-first) + 0x7E
            // For single byte 0x00, QCDM FCS-16 (init 0xFFFF, reflected 0x8408, XOR 0xFFFF) = 0x1D14 -> bytes 0x14, 0x1D? Let's compute exact PPP FCS-16!
            var req = BuildQcdmFrame([0x00]);
            await channel.WriteAsync(req, ct).ConfigureAwait(false);

            var buf = new byte[128];
            int n = await channel.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n >= 40 && buf[0] == 0x00)
            {
                var compDate = Encoding.ASCII.GetString(buf, 1, 11).Trim('\0', ' ');
                var verDir = Encoding.ASCII.GetString(buf, 20, 8).Trim('\0', ' ');
                var summary = $"Read-only DIAG_VERNO_F succeeded on {port}: CompileDate={compDate}, BuildDir={verDir}";
                _logger.LogInfo(ProviderName, $"DIAG_VERNO_F ({port})", summary, endpoint.UsbDevice.InstanceId, "diag.readonly_version_query");
                return new DiagReadOnlyQueryResult(true, true, endpoint.ProtocolFamily, compDate, verDir, summary);
            }

            return new DiagReadOnlyQueryResult(true, false, endpoint.ProtocolFamily, null, null, $"Sent read-only DIAG_VERNO_F on {port}, received {n} bytes (port may require OEM diagnostic unlock).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ProviderName, $"DIAG_VERNO_F ({port})", ex.Message, null, endpoint.UsbDevice.InstanceId, "diag.readonly_version_query");
            return new DiagReadOnlyQueryResult(true, false, endpoint.ProtocolFamily, null, null, $"DIAG query failed on {port}: {ex.Message}");
        }
    }

    public static byte[] BuildQcdmFrame(ReadOnlySpan<byte> payload)
    {
        ushort fcs = 0xFFFF;
        foreach (byte b in payload)
        {
            fcs = (ushort)((fcs >> 8) ^ ReflectPppByte((byte)((fcs ^ b) & 0xFF)));
        }
        fcs ^= 0xFFFF;

        var list = new List<byte>(payload.Length + 4);
        AppendEscaped(list, payload);
        AppendEscaped(list, [(byte)(fcs & 0xFF), (byte)(fcs >> 8)]);
        list.Add(0x7E);
        return list.ToArray();
    }

    private static ushort ReflectPppByte(byte index)
    {
        ushort v = index;
        for (int i = 0; i < 8; i++)
        {
            v = (v & 1) != 0 ? (ushort)((v >> 1) ^ 0x8408) : (ushort)(v >> 1);
        }
        return v;
    }

    private static void AppendEscaped(List<byte> dest, ReadOnlySpan<byte> src)
    {
        foreach (byte b in src)
        {
            if (b is 0x7E or 0x7D)
            {
                dest.Add(0x7D);
                dest.Add((byte)(b ^ 0x20));
            }
            else
            {
                dest.Add(b);
            }
        }
    }
}
