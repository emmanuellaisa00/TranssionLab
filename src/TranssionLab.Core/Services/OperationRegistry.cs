using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;

namespace TranssionLab.Core.Services;

/// <summary>
/// Central declarative catalog of all servicing operations (Section 4).
/// UI views query this registry via IProtocolCapabilityService so unavailable operations are never offered.
/// </summary>
public sealed class OperationRegistry : IOperationRegistry
{
    private readonly List<OperationDescriptor> _operations;

    public OperationRegistry()
    {
        _operations =
        [
            new OperationDescriptor
            {
                OperationId = "adb.read_identity",
                DisplayName = "Read Verified Device Properties (ADB)",
                Category = "ADB",
                Description = "Queries ro.product.*, ro.build.*, slot, AVB and OEM unlock status over authorized ADB.",
                RequiredConnectionModes = [ConnectionMode.AdbAuthorized],
                RequiresVerifiedDeviceProfile = false,
                RequiredAuthorization = AuthorizationRequirement.AdbRsaAuthorized,
                MinAndroidApiOrVersion = 8,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = false,
                MayEraseUserData = false,
                IsDestructive = false,
                SupportsSafeCancellation = true,
                VerificationMethod = "Cross-checks ro.build.fingerprint and ro.product.model exit code 0.",
                KnownLimitations = ["Requires USB debugging enabled and host RSA key accepted on the phone screen."]
            },
            new OperationDescriptor
            {
                OperationId = "adb.diagnostics_health",
                DisplayName = "Capture Battery & Partition Storage Diagnostics",
                Category = "ADB",
                Description = "Runs read-only dumpsys battery and df -h partition inspection.",
                RequiredConnectionModes = [ConnectionMode.AdbAuthorized],
                RequiresVerifiedDeviceProfile = false,
                RequiredAuthorization = AuthorizationRequirement.AdbRsaAuthorized,
                MinAndroidApiOrVersion = 8,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = false,
                MayEraseUserData = false,
                IsDestructive = false,
                SupportsSafeCancellation = true,
                VerificationMethod = "Parses structured battery scale/level and mounted filesystem blocks.",
                KnownLimitations = ["SELinux restricts raw block device reads without root; reports OS-visible stats only."]
            },
            new OperationDescriptor
            {
                OperationId = "adb.reboot_mode",
                DisplayName = "Transition Device Mode (System / Bootloader / Fastbootd / Recovery)",
                Category = "ADB",
                Description = "Requests clean reboot into bootloader, userspace fastbootd, or Android recovery.",
                RequiredConnectionModes = [ConnectionMode.AdbAuthorized, ConnectionMode.Recovery],
                RequiresVerifiedDeviceProfile = false,
                RequiredAuthorization = AuthorizationRequirement.AdbRsaAuthorized,
                MinAndroidApiOrVersion = 8,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = false,
                MayEraseUserData = false,
                IsDestructive = false,
                SupportsSafeCancellation = false,
                VerificationMethod = "Monitors USB re-enumeration into target VID/PID mode.",
                KnownLimitations = ["Fastbootd requires Android 10+ dynamic partitions support."]
            },
            new OperationDescriptor
            {
                OperationId = "recovery.ota_sideload",
                DisplayName = "Sideload Verified Stock OTA Package",
                Category = "Recovery",
                Description = "Streams an OEM-signed OTA ZIP package to Android Recovery over adb sideload.",
                RequiredConnectionModes = [ConnectionMode.AdbSideload],
                RequiresVerifiedDeviceProfile = true,
                RequiredAuthorization = AuthorizationRequirement.None,
                MinAndroidApiOrVersion = 8,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.SignedOtaZip,
                ModifiesDeviceState = true,
                MayEraseUserData = false,
                IsDestructive = false,
                SupportsSafeCancellation = false,
                VerificationMethod = "Recovery verifies OEM RSA/AVB signature before applying blocks; exit status checked.",
                KnownLimitations = ["Device recovery rejects unsigned, downgraded (anti-rollback), or mismatched OTA packages."]
            },
            new OperationDescriptor
            {
                OperationId = "fastboot.read_vars",
                DisplayName = "Inspect Bootloader / Fastbootd Variables",
                Category = "Fastboot",
                Description = "Queries getvar all, unlocked state, active slot, is-userspace, and max-download-size.",
                RequiredConnectionModes = [ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
                RequiresVerifiedDeviceProfile = false,
                RequiredAuthorization = AuthorizationRequirement.None,
                MinAndroidApiOrVersion = 8,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = false,
                MayEraseUserData = false,
                IsDestructive = false,
                SupportsSafeCancellation = true,
                VerificationMethod = "Parses fastboot stderr/stdout OKAY protocol trailer.",
                KnownLimitations = ["Some Transsion bootloaders restrict getvar all output and require per-variable queries."]
            },
            new OperationDescriptor
            {
                OperationId = "fastboot.set_active_slot",
                DisplayName = "Switch Active A/B Boot Slot",
                Category = "Fastboot",
                Description = "Switches active boot slot (a or b) on A/B and Virtual A/B devices.",
                RequiredConnectionModes = [ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
                RequiresVerifiedDeviceProfile = true,
                RequiredAuthorization = AuthorizationRequirement.BootloaderUnlocked,
                MinAndroidApiOrVersion = 10,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = true,
                MayEraseUserData = false,
                IsDestructive = false,
                SupportsSafeCancellation = false,
                VerificationMethod = "Re-queries fastboot getvar current-slot to confirm slot change.",
                KnownLimitations = ["Locked bootloaders reject set_active; switching to an unbootable slot can prevent boot."]
            },
            new OperationDescriptor
            {
                OperationId = "bootloader.verified_unlock",
                DisplayName = "Official Bootloader Unlock",
                Category = "Bootloader",
                Description = "Executes documented fastboot flashing unlock (or profile-specified oem unlock) after OEM Unlocking is enabled in Developer Options.",
                RequiredConnectionModes = [ConnectionMode.FastbootBootloader],
                RequiresVerifiedDeviceProfile = true,
                RequiredAuthorization = AuthorizationRequirement.None,
                MinAndroidApiOrVersion = 9,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = true,
                MayEraseUserData = true,
                IsDestructive = true,
                SupportsSafeCancellation = false,
                VerificationMethod = "Re-queries fastboot getvar unlocked after user volume-key confirmation on handset.",
                KnownLimitations =
                [
                    "Triggers mandatory cryptographic factory reset erasing all userdata.",
                    "Requires 'OEM unlocking' toggle enabled in Android Settings -> Developer Options.",
                    "Android 16 does not guarantee unlockability; carrier-locked or region-restricted Transsion models may block unlock in LK."
                ]
            },
            new OperationDescriptor
            {
                OperationId = "flash.fastboot_partition",
                DisplayName = "Validated Partition Flash (Fastboot / Fastbootd)",
                Category = "Flash",
                Description = "Executes 11-step validated partition flashing for compatible images, enforcing logical partition mode and NV protection.",
                RequiredConnectionModes = [ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
                RequiresVerifiedDeviceProfile = true,
                RequiredAuthorization = AuthorizationRequirement.BootloaderUnlocked,
                MinAndroidApiOrVersion = 9,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.SinglePartitionImage,
                ModifiesDeviceState = true,
                MayEraseUserData = false,
                IsDestructive = true,
                SupportsSafeCancellation = true,
                VerificationMethod = "Validates image size <= partition-size, checks fastboot OKAY response, and re-verifies device variables.",
                KnownLimitations =
                [
                    "Logical partitions inside super (system, vendor, product, system_ext) require Fastbootd userspace mode.",
                    "Protected calibration partitions (nvram, nvdata, persist, proinfo, seccfg) are strictly blocked."
                ]
            },
            new OperationDescriptor
            {
                OperationId = "partition.inspect_table",
                DisplayName = "Inspect Live GPT & Dynamic Super Partition Table",
                Category = "Partition",
                Description = "Enumerates physical and logical dynamic partitions, capacities, slot suffixes, and safety policies across Fastboot, Fastbootd, ADB, or Recovery.",
                RequiredConnectionModes =
                [
                    ConnectionMode.FastbootBootloader,
                    ConnectionMode.FastbootdUserspace,
                    ConnectionMode.AdbAuthorized,
                    ConnectionMode.Recovery
                ],
                RequiresVerifiedDeviceProfile = false,
                RequiredAuthorization = AuthorizationRequirement.None,
                MinAndroidApiOrVersion = 8,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = false,
                MayEraseUserData = false,
                IsDestructive = false,
                SupportsSafeCancellation = true,
                VerificationMethod = "Cross-checks fastboot partition-size/is-logical or /dev/block/by-name nodes.",
                KnownLimitations = ["Locked retail ADB shells restrict raw block sizes under SELinux unless running in Recovery or debuggable shell."]
            },
            new OperationDescriptor
            {
                OperationId = "partition.manage_logical_super",
                DisplayName = "Manage Dynamic Logical Partitions in Super (Create / Resize / Delete)",
                Category = "Partition",
                Description = "Creates, resizes, or deletes dynamic logical sub-partitions inside super (e.g. system_a, product_a, tr_mi_a) in Userspace Fastbootd.",
                RequiredConnectionModes = [ConnectionMode.FastbootdUserspace],
                RequiresVerifiedDeviceProfile = false,
                RequiredAuthorization = AuthorizationRequirement.BootloaderUnlocked,
                MinAndroidApiOrVersion = 10,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = true,
                MayEraseUserData = false,
                IsDestructive = true,
                SupportsSafeCancellation = false,
                VerificationMethod = "Re-queries fastboot getvar is-logical:<name> and partition-size:<name> after modification.",
                KnownLimitations = ["Strictly requires Userspace Fastbootd (is-userspace: yes) and an unlocked bootloader."]
            },
            new OperationDescriptor
            {
                OperationId = "partition.erase_safe",
                DisplayName = "Controlled Partition Erase (Cache / Metadata / Explicit Userdata)",
                Category = "Partition",
                Description = "Executes controlled fastboot erase on permitted partitions while permanently blocking FRP, NVRAM/calibration, and boot-chain partitions.",
                RequiredConnectionModes = [ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
                RequiresVerifiedDeviceProfile = false,
                RequiredAuthorization = AuthorizationRequirement.BootloaderUnlocked,
                MinAndroidApiOrVersion = 9,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = true,
                MayEraseUserData = true,
                IsDestructive = true,
                SupportsSafeCancellation = false,
                VerificationMethod = "Verifies fastboot OKAY status and target responsiveness.",
                KnownLimitations = ["Erasing frp, nvram, nvdata, persist, proinfo, preloader, or lk is permanently prohibited."]
            },
            new OperationDescriptor
            {
                OperationId = "partition.adb_readback",
                DisplayName = "Authorized Partition Block Readback / Backup (.img)",
                Category = "Partition",
                Description = "Streams unprotected partition block nodes to a local .img backup with SHA-256 verification when shell permissions allow (Recovery / authorized root).",
                RequiredConnectionModes = [ConnectionMode.AdbAuthorized, ConnectionMode.Recovery],
                RequiresVerifiedDeviceProfile = false,
                RequiredAuthorization = AuthorizationRequirement.None,
                MinAndroidApiOrVersion = 8,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = false,
                MayEraseUserData = false,
                IsDestructive = false,
                SupportsSafeCancellation = true,
                VerificationMethod = "Computes SHA-256 and verifies non-zero byte count of pulled partition image.",
                KnownLimitations = ["Unprivileged retail user builds enforce SELinux block read restrictions; FRP and userdata extraction are blocked."]
            },
            new OperationDescriptor
            {
                OperationId = "mtk.brom_readonly_probe",
                DisplayName = "MediaTek BROM / Preloader Security & Chipset Probe",
                Category = "MediaTek",
                Description = "Performs documented read-only BROM handshake (GET_HW_CODE 0xFD, GET_TARGET_CONFIG 0xD8) to identify SoC code and SLA/DAA/SBC flags.",
                RequiredConnectionModes = [ConnectionMode.MediaTekBrom, ConnectionMode.MediaTekPreloader],
                RequiresVerifiedDeviceProfile = false,
                RequiredAuthorization = AuthorizationRequirement.None,
                MinAndroidApiOrVersion = 8,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = false,
                MayEraseUserData = false,
                IsDestructive = false,
                SupportsSafeCancellation = true,
                VerificationMethod = "Validates 4-byte sync echo 5F F5 AF FA and 16-bit status word <= 0x00FF.",
                KnownLimitations =
                [
                    "Preloader VCOM window typically lasts 2–3 seconds unless held or already in BROM.",
                    "Does NOT bypass SLA/DAA authentication or upload unauthorized Download Agents."
                ]
            },
            new OperationDescriptor
            {
                OperationId = "meta.diagnose_interface",
                DisplayName = "Verify META Mode Interface & Provider Readiness",
                Category = "META",
                Description = "Validates MediaTek META USB descriptors, COM mapping, and external licensed META library availability.",
                RequiredConnectionModes = [ConnectionMode.MediaTekMeta],
                RequiresVerifiedDeviceProfile = false,
                RequiredAuthorization = AuthorizationRequirement.LicensedMetaLibrary,
                MinAndroidApiOrVersion = 8,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = false,
                MayEraseUserData = false,
                IsDestructive = false,
                SupportsSafeCancellation = true,
                VerificationMethod = "Checks USB VID/PID/MI against META descriptors and verifies local DLL signature/presence.",
                KnownLimitations = ["Active AP/Modem META commands require OEM-authorized ModemMETA libraries not redistributed with TranssionLab."]
            },
            new OperationDescriptor
            {
                OperationId = "unisoc.boot_version_probe",
                DisplayName = "Unisoc Download Mode Read-Only Version Handshake",
                Category = "Unisoc",
                Description = "Sends documented HDLC 0x7E baud check over SCI USB2Serial to read BSL_REP_VER boot ROM banner.",
                RequiredConnectionModes = [ConnectionMode.UnisocDownload],
                RequiresVerifiedDeviceProfile = false,
                RequiredAuthorization = AuthorizationRequirement.None,
                MinAndroidApiOrVersion = 8,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = false,
                MayEraseUserData = false,
                IsDestructive = false,
                SupportsSafeCancellation = true,
                VerificationMethod = "Validates HDLC 0x7E framing and CRC-16 checksum on BSL_REP_VER (0x81) packet.",
                KnownLimitations = ["Flashing partitions on Secure Boot Unisoc SoCs (T606/T612/T616) additionally requires OEM-signed FDL1 and FDL2 binaries."]
            },
            new OperationDescriptor
            {
                OperationId = "diag.readonly_version_query",
                DisplayName = "Read-Only Diagnostic Interface Version Probe",
                Category = "DIAG",
                Description = "Identifies DIAG interface chipset family and runs documented read-only version query when supported.",
                RequiredConnectionModes = [ConnectionMode.QualcommDiag, ConnectionMode.UnisocDiag, ConnectionMode.VendorDiag],
                RequiresVerifiedDeviceProfile = false,
                RequiredAuthorization = AuthorizationRequirement.VerifiedDiagnosticInterface,
                MinAndroidApiOrVersion = 8,
                MaxAndroidVersion = 16,
                RequiredFirmware = FirmwareRequirement.None,
                ModifiesDeviceState = false,
                MayEraseUserData = false,
                IsDestructive = false,
                SupportsSafeCancellation = true,
                VerificationMethod = "Validates CRC-16 CCITT frame response on documented DIAG_VERNO_F (0x00) where Qualcomm DIAG is confirmed.",
                KnownLimitations = ["Most TECNO/Infinix/itel devices use MediaTek or Unisoc chipsets and do NOT implement Qualcomm QCDM; NV writes are blocked."]
            }
        ];
    }

    public IReadOnlyList<OperationDescriptor> GetAllOperations() => _operations;

    public OperationDescriptor? GetById(string operationId) =>
        _operations.FirstOrDefault(o => string.Equals(o.OperationId, operationId, StringComparison.OrdinalIgnoreCase));
}
