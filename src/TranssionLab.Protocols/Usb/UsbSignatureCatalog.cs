using System.Globalization;
using System.Text.RegularExpressions;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Models;

namespace TranssionLab.Protocols.Usb;

/// <summary>
/// Authoritative USB VID/PID/MI classifier and Windows PnP driver diagnostic rules
/// for Transsion (TECNO/Infinix/itel), Google Android, MediaTek, Unisoc/Spreadtrum, and Qualcomm interfaces.
/// </summary>
public static partial class UsbSignatureCatalog
{
    public const ushort VidGoogle = 0x18D1;
    public const ushort VidTranssion = 0x2ECC;
    public const ushort VidMediaTek = 0x0E8D;
    public const ushort VidMtkLkFastboot = 0x0BB4;
    public const ushort VidUnisoc = 0x1782;
    public const ushort VidQualcomm = 0x05C6;

    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})(?:&MI_([0-9A-Fa-f]{2}))?")]
    private static partial Regex VidPidMiRegex();

    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ComPortRegex();

    public static bool TryParsePnpDeviceId(
        string pnpDeviceId,
        out ushort vid,
        out ushort pid,
        out int? mi,
        out string? serialOrInstanceTail)
    {
        vid = 0;
        pid = 0;
        mi = null;
        serialOrInstanceTail = null;

        if (string.IsNullOrWhiteSpace(pnpDeviceId))
            return false;

        var m = VidPidMiRegex().Match(pnpDeviceId);
        if (!m.Success)
            return false;

        vid = ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        pid = ushort.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (m.Groups[3].Success)
        {
            mi = int.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        var parts = pnpDeviceId.Split('\\');
        if (parts.Length >= 3)
        {
            var tail = parts[^1];
            // Composite parent suffixes on Windows contain '&' when OS-synthesized rather than hardware serial
            if (!tail.Contains('&'))
            {
                serialOrInstanceTail = tail;
            }
        }

        return true;
    }

    public static string? ExtractComPortFromName(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
            return null;
        var m = ComPortRegex().Match(friendlyName);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    public static (ConnectionMode Mode, ChipsetVendor Chipset, string VendorLabel) ClassifyInterface(
        ushort vid,
        ushort pid,
        int? mi,
        string? friendlyName)
    {
        var nameUpper = (friendlyName ?? string.Empty).ToUpperInvariant();

        // 1. MediaTek (0x0E8D)
        if (vid == VidMediaTek)
        {
            return pid switch
            {
                0x0003 => (ConnectionMode.MediaTekBrom, ChipsetVendor.MediaTek, "MediaTek Inc. (Boot ROM)"),
                0x2000 => (ConnectionMode.MediaTekPreloader, ChipsetVendor.MediaTek, "MediaTek Inc. (Preloader VCOM)"),
                0x2001 => (ConnectionMode.MediaTekDownloadAgent, ChipsetVendor.MediaTek, "MediaTek Inc. (DA VCOM)"),
                0x2006 or 0x2007 or 0x200A or 0x200D =>
                    nameUpper.Contains("ADB") && mi == 1
                        ? (ConnectionMode.AdbAuthorized, ChipsetVendor.MediaTek, "MediaTek Composite ADB")
                        : (ConnectionMode.MediaTekMeta, ChipsetVendor.MediaTek, "MediaTek META / ETS Interface"),
                0x201C => (ConnectionMode.FastbootBootloader, ChipsetVendor.MediaTek, "MediaTek Fastboot"),
                _ when nameUpper.Contains("META") => (ConnectionMode.MediaTekMeta, ChipsetVendor.MediaTek, "MediaTek META Interface"),
                _ when nameUpper.Contains("PRELOADER") => (ConnectionMode.MediaTekPreloader, ChipsetVendor.MediaTek, "MediaTek Preloader"),
                _ when nameUpper.Contains("ELT") || nameUpper.Contains("DIAG") => (ConnectionMode.VendorDiag, ChipsetVendor.MediaTek, "MediaTek ELT Diagnostic"),
                _ => (ConnectionMode.CompositeUsb, ChipsetVendor.MediaTek, "MediaTek USB Device")
            };
        }

        // 2. Spreadtrum / Unisoc (0x1782)
        if (vid == VidUnisoc)
        {
            return pid switch
            {
                0x4D00 => (ConnectionMode.UnisocDownload, ChipsetVendor.Unisoc, "Unisoc / Spreadtrum Download Boot (U2S Diag)"),
                0x4D10 or 0x4D12 or 0x3D00 => (ConnectionMode.UnisocDiag, ChipsetVendor.Unisoc, "Unisoc Diagnostic / AT Interface"),
                0x4001 or 0x4002 or 0x4003 =>
                    mi == 1 || nameUpper.Contains("ADB")
                        ? (ConnectionMode.AdbAuthorized, ChipsetVendor.Unisoc, "Unisoc ADB Interface")
                        : (ConnectionMode.MtpOnly, ChipsetVendor.Unisoc, "Unisoc MTP / Composite"),
                _ when nameUpper.Contains("DIAG") || nameUpper.Contains("SPRD") => (ConnectionMode.UnisocDiag, ChipsetVendor.Unisoc, "Unisoc Diagnostic Port"),
                _ => (ConnectionMode.CompositeUsb, ChipsetVendor.Unisoc, "Unisoc USB Device")
            };
        }

        // 3. Transsion Holdings (0x2ECC — TECNO, Infinix, itel)
        if (vid == VidTranssion)
        {
            if (nameUpper.Contains("FASTBOOT") || nameUpper.Contains("BOOTLOADER") || pid is 0x201C or 0xD00D)
                return (ConnectionMode.FastbootBootloader, ChipsetVendor.Unknown, "Transsion Holdings (Fastboot)");
            if (nameUpper.Contains("SIDELOAD"))
                return (ConnectionMode.AdbSideload, ChipsetVendor.Unknown, "Transsion Holdings (Recovery Sideload)");
            if (nameUpper.Contains("ADB") || mi == 1)
                return (ConnectionMode.AdbAuthorized, ChipsetVendor.Unknown, "Transsion Holdings (ADB Interface)");
            return (ConnectionMode.MtpOnly, ChipsetVendor.Unknown, "Transsion Holdings (TECNO / Infinix / itel USB)");
        }

        // 4. Google Standard Android / AOSP (0x18D1) & MTK LK Fastboot (0x0BB4)
        if (vid is VidGoogle or VidMtkLkFastboot)
        {
            if (pid is 0x4EE0 or 0xD00D or 0x0C01 || nameUpper.Contains("FASTBOOT") || nameUpper.Contains("BOOTLOADER"))
                return (ConnectionMode.FastbootBootloader, ChipsetVendor.Unknown, "Android Bootloader / Fastboot Interface");
            if (pid is 0xD001 || nameUpper.Contains("SIDELOAD") || nameUpper.Contains("RECOVERY"))
                return (ConnectionMode.AdbSideload, ChipsetVendor.Unknown, "Android Recovery / Sideload Interface");
            if (pid is 0x4EE2 or 0x4EE7 || nameUpper.Contains("ADB"))
                return (ConnectionMode.AdbAuthorized, ChipsetVendor.Unknown, "Android ADB Interface");
            if (pid is 0x4EE1)
                return (ConnectionMode.MtpOnly, ChipsetVendor.Unknown, "Android MTP Device");
        }

        // 5. Qualcomm (0x05C6)
        if (vid == VidQualcomm)
        {
            if (pid is 0x900E or 0x9091 or 0x901D || nameUpper.Contains("DIAG") || nameUpper.Contains("9091"))
                return (ConnectionMode.QualcommDiag, ChipsetVendor.Qualcomm, "Qualcomm HS-USB Diagnostics");
        }

        return (ConnectionMode.Unknown, ChipsetVendor.Unknown, "Unclassified USB Device");
    }

    public static bool IsRelevantServicingDevice(ushort vid, string? friendlyName)
    {
        if (vid is VidTranssion or VidGoogle or VidMediaTek or VidMtkLkFastboot or VidUnisoc or VidQualcomm)
            return true;

        var upper = (friendlyName ?? string.Empty).ToUpperInvariant();
        return upper.Contains("TECNO") || upper.Contains("INFINIX") || upper.Contains("ITEL") ||
               upper.Contains("ANDROID") || upper.Contains("ADB") || upper.Contains("FASTBOOT") ||
               upper.Contains("MEDIATEK") || upper.Contains("PRELOADER") || upper.Contains("UNISOC") ||
               upper.Contains("SPREADTRUM") || upper.Contains("SPRD");
    }

    public static DriverDiagnosticInfo EvaluateDriverHealth(
        int? configManagerErrorCode,
        string? serviceName,
        string? manufacturer,
        ConnectionMode mode)
    {
        var code = configManagerErrorCode ?? 0;
        return code switch
        {
            0 => new DriverDiagnosticInfo(
                DriverHealthStatus.Healthy,
                0,
                serviceName,
                manufacturer,
                null,
                $"Driver active ({serviceName ?? "kernel-bound"}).",
                "No driver action required."),
            28 => new DriverDiagnosticInfo(
                DriverHealthStatus.MissingDriverCode28,
                28,
                serviceName,
                manufacturer,
                null,
                "Windows Error Code 28: No compatible driver is installed for this interface.",
                GetSafeMissingDriverGuidance(mode)),
            10 => new DriverDiagnosticInfo(
                DriverHealthStatus.CannotStartCode10,
                10,
                serviceName,
                manufacturer,
                null,
                "Windows Error Code 10: This device cannot start.",
                mode == ConnectionMode.MediaTekPreloader
                    ? "Common on MediaTek Preloader when the 2–3 second boot window expires and the phone begins normal charging. Reconnect while powered off if probing BROM/Preloader."
                    : "Inspect USB cable integrity, avoid unpowered USB hubs, and verify driver architecture matches Windows x64/ARM64."),
            22 => new DriverDiagnosticInfo(
                DriverHealthStatus.DisabledCode22,
                22,
                serviceName,
                manufacturer,
                null,
                "Windows Error Code 22: Device is disabled in Windows Device Manager.",
                "Enable the device manually in Windows Device Manager if intended."),
            43 => new DriverDiagnosticInfo(
                DriverHealthStatus.DescriptorFailureCode43,
                43,
                serviceName,
                manufacturer,
                null,
                "Windows Error Code 43: USB device descriptor request failed.",
                "Typically caused by a damaged USB-C cable, dirty connector pins, or unstable USB 3.x controller port. Test with a shielded USB 2.0 port and short data cable."),
            52 => new DriverDiagnosticInfo(
                DriverHealthStatus.UnsignedBlockedCode52,
                52,
                serviceName,
                manufacturer,
                null,
                "Windows Error Code 52: Windows cannot verify the digital signature for the drivers required for this device.",
                "Do NOT disable Windows Driver Signature Enforcement. Replace the unsigned driver with an WHQL/Microsoft-signed WinUSB, Google USB Driver, MediaTek USB VCOM, or Unisoc SCI USB2Serial package."),
            _ => new DriverDiagnosticInfo(
                DriverHealthStatus.OtherProblem,
                code,
                serviceName,
                manufacturer,
                null,
                $"Windows ConfigManager reported problem code {code}.",
                "Inspect Device Manager -> Properties -> Events tab for the specific INF binding error.")
        };
    }

    private static string GetSafeMissingDriverGuidance(ConnectionMode mode) => mode switch
    {
        ConnectionMode.FastbootBootloader or ConnectionMode.FastbootdUserspace =>
            "Install the WHQL-signed Google USB Driver ('Android Bootloader Interface' via WinUSB.sys) in Windows Device Manager -> Update Driver.",
        ConnectionMode.MediaTekBrom or ConnectionMode.MediaTekPreloader or ConnectionMode.MediaTekMeta =>
            "Install the WHQL-signed MediaTek USB VCOM (usbser.sys) driver package from an official OEM servicing distribution.",
        ConnectionMode.UnisocDownload or ConnectionMode.UnisocDiag =>
            "Install the WHQL-signed Unisoc / Spreadtrum SCI USB2Serial driver package.",
        _ =>
            "Install the signed Google ADB/WinUSB interface driver via Windows Update Optional Drivers or official Android SDK usb_driver."
    };
}
