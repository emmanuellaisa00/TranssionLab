using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;

namespace TranssionLab.Protocols.Discovery;

public sealed class ProtocolCapabilityService : IProtocolCapabilityService
{
    private readonly IUsbInterfaceProvider _usbProvider;
    private readonly IAdbProvider _adbProvider;
    private readonly IRecoveryProvider _recoveryProvider;
    private readonly IFastbootProvider _fastbootProvider;
    private readonly IMediaTekProvider _mtkProvider;
    private readonly IMetaModeProvider _metaProvider;
    private readonly ISpdUnisocProvider _unisocProvider;
    private readonly IDiagnosticProvider _diagProvider;
    private readonly IOperationRegistry _operationRegistry;

    public ProtocolCapabilityService(
        IUsbInterfaceProvider usbProvider,
        IAdbProvider adbProvider,
        IRecoveryProvider recoveryProvider,
        IFastbootProvider fastbootProvider,
        IMediaTekProvider mtkProvider,
        IMetaModeProvider metaProvider,
        ISpdUnisocProvider unisocProvider,
        IDiagnosticProvider diagProvider,
        IOperationRegistry operationRegistry)
    {
        _usbProvider = usbProvider;
        _adbProvider = adbProvider;
        _recoveryProvider = recoveryProvider;
        _fastbootProvider = fastbootProvider;
        _mtkProvider = mtkProvider;
        _metaProvider = metaProvider;
        _unisocProvider = unisocProvider;
        _diagProvider = diagProvider;
        _operationRegistry = operationRegistry;
    }

    public IReadOnlyList<ProtocolCapabilitySummary> GetAllProviderSummaries()
    {
        var hasMetaLib = _metaProvider.IsAuthorizedMetaLibraryInstalled(out var metaDetail);
        return
        [
            new ProtocolCapabilitySummary(
                _usbProvider.ProviderName,
                ConnectionMode.CompositeUsb,
                _usbProvider.ImplementationStatus,
                HardwareVerificationLevel.SimulatedUnitTestedOnly,
                true,
                OperatingSystem.IsWindows() ? "Windows WMI / PnP & SerialPort active" : "Simulated / Non-Windows host mode",
                ["VID/PID/MI enumeration", "PnP error code diagnostics (Code 10/28/43/52)", "COM port mapping", "Arrival/removal history"],
                ["Never installs unsigned drivers or modifies Windows boot signature policy"]),

            new ProtocolCapabilitySummary(
                _adbProvider.ProviderName,
                ConnectionMode.AdbAuthorized,
                _adbProvider.ImplementationStatus,
                HardwareVerificationLevel.SimulatedUnitTestedOnly,
                _adbProvider.ImplementationStatus != ProviderImplementationStatus.Unavailable,
                _adbProvider.ImplementationStatus != ProviderImplementationStatus.Unavailable ? "Android platform-tools adb binary verified" : "adb executable not found on PATH or Settings",
                ["Authorized/unauthorized/offline/sideload detection", "Verified getprop identity & Android 8–16 checks", "Read-only battery & storage health", "Mode reboot transitions"],
                ["Requires user acceptance of RSA debugging prompt on handset"]),

            new ProtocolCapabilitySummary(
                _recoveryProvider.ProviderName,
                ConnectionMode.AdbSideload,
                _recoveryProvider.ImplementationStatus,
                HardwareVerificationLevel.SimulatedUnitTestedOnly,
                _recoveryProvider.ImplementationStatus != ProviderImplementationStatus.Unavailable,
                "Uses verified adb sideload transport",
                ["Recovery & sideload endpoint detection", "ZIP OTA magic verification & sideload streaming"],
                ["Handset recovery enforces OEM cryptographic signature and anti-rollback checks"]),

            new ProtocolCapabilitySummary(
                _fastbootProvider.ProviderName,
                ConnectionMode.FastbootBootloader,
                _fastbootProvider.ImplementationStatus,
                HardwareVerificationLevel.SimulatedUnitTestedOnly,
                _fastbootProvider.ImplementationStatus != ProviderImplementationStatus.Unavailable,
                _fastbootProvider.ImplementationStatus != ProviderImplementationStatus.Unavailable ? "Android platform-tools fastboot binary verified" : "fastboot executable not found on PATH or Settings",
                ["Bootloader vs Userspace Fastbootd differentiation (is-userspace)", "getvar inspection", "A/B slot switching", "11-step validated partition flashing", "Official bootloader unlock workflow"],
                ["Logical partitions inside super require Fastbootd; protected NV partitions are blocked"]),

            new ProtocolCapabilitySummary(
                _mtkProvider.ProviderName,
                ConnectionMode.MediaTekBrom,
                _mtkProvider.ImplementationStatus,
                HardwareVerificationLevel.SimulatedUnitTestedOnly,
                true,
                "Built-in serial BROM/Preloader framing + Scatter parser",
                ["Preloader/BROM/DA USB detection", "Read-only BROM 4-byte sync & GET_HW_CODE (0xFD)", "GET_TARGET_CONFIG (0xD8) SBC/SLA/DAA flag inspection", "Text & XML Scatter validation"],
                ["SLA/DAA secure download requires OEM-signed Download Agent; BROM exploits/bypasses are strictly excluded"]),

            new ProtocolCapabilitySummary(
                _metaProvider.ProviderName,
                ConnectionMode.MediaTekMeta,
                _metaProvider.ImplementationStatus,
                HardwareVerificationLevel.SimulatedUnitTestedOnly,
                hasMetaLib,
                metaDetail,
                ["META USB VID/PID/MI detection", "VCOM driver health check", "Provider readiness verification"],
                ["Proprietary MediaTek ModemMETA DLLs are not bundled; NVRAM/IMEI edits are prohibited"]),

            new ProtocolCapabilitySummary(
                _unisocProvider.ProviderName,
                ConnectionMode.UnisocDownload,
                _unisocProvider.ImplementationStatus,
                HardwareVerificationLevel.SimulatedUnitTestedOnly,
                true,
                "Built-in HDLC 0x7E framing, CRC-16 XMODEM & PAC binary parser",
                ["Unisoc 0x1782 Download/DIAG detection", "Read-only HDLC BSL_CMD_CHECK_BAUD -> BSL_REP_VER banner probe", "Binary .pac container inspection & extraction"],
                ["Secure Boot Unisoc SoCs (T603/T606/T612/T616) require OEM-signed FDL1/FDL2 for flashing"]),

            new ProtocolCapabilitySummary(
                _diagProvider.ProviderName,
                ConnectionMode.QualcommDiag,
                _diagProvider.ImplementationStatus,
                HardwareVerificationLevel.SimulatedUnitTestedOnly,
                true,
                "Built-in chipset-aware DIAG classifier & read-only QCDM framing",
                ["Qualcomm vs Unisoc vs MediaTek DIAG classification", "Read-only DIAG_VERNO_F (0x00) query on confirmed Qualcomm DIAG"],
                ["Never sends Qualcomm QCDM commands to MediaTek/Unisoc devices; NV writes are blocked"])
        ];
    }

    public IReadOnlyList<OperationCapabilityEvaluation> EvaluateOperationsForDevice(ConnectedDeviceSnapshot? device)
    {
        var ops = _operationRegistry.GetAllOperations();
        var results = new List<OperationCapabilityEvaluation>(ops.Count);

        foreach (var op in ops)
        {
            if (device is null)
            {
                results.Add(new OperationCapabilityEvaluation(
                    op,
                    false,
                    "No active device selected.",
                    ["Connect and explicitly select a target device first."]));
                continue;
            }

            var unmet = new List<string>();

            if (!op.RequiredConnectionModes.Contains(device.ActiveConnectionMode))
            {
                unmet.Add($"Requires mode [{string.Join(" or ", op.RequiredConnectionModes)}]; device is in {device.ActiveConnectionMode}.");
            }

            if (op.RequiresVerifiedDeviceProfile && string.IsNullOrWhiteSpace(device.MatchedProfileId))
            {
                unmet.Add("Requires a matched Device Profile (TECNO / Infinix / itel) before execution.");
            }

            switch (op.RequiredAuthorization)
            {
                case AuthorizationRequirement.AdbRsaAuthorized when device.ActiveConnectionMode != ConnectionMode.AdbAuthorized:
                    unmet.Add("Requires authorized ADB session (accept RSA debugging prompt on handset).");
                    break;
                case AuthorizationRequirement.BootloaderUnlocked when device.BootloaderState.Value != BootloaderLockState.Unlocked:
                    unmet.Add($"Requires unlocked bootloader (current state: {device.BootloaderState.Value}).");
                    break;
                case AuthorizationRequirement.LicensedMetaLibrary when !_metaProvider.IsAuthorizedMetaLibraryInstalled(out _):
                    unmet.Add("Requires licensed external META communication library configured in Settings.");
                    break;
            }

            var available = unmet.Count == 0;
            var reason = available
                ? $"Available for {device.ActiveConnectionMode}"
                : unmet[0];

            results.Add(new OperationCapabilityEvaluation(op, available, reason, unmet));
        }

        return results;
    }
}
