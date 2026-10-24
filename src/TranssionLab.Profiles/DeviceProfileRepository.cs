using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;

namespace TranssionLab.Profiles;

/// <summary>
/// Offline-first repository of TECNO, Infinix, and itel device profiles.
/// Never matches a phone's exact model from chipset alone; requires exact model code, codename, or build fingerprint match.
/// </summary>
public sealed class DeviceProfileRepository : IDeviceProfileRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _customProfilesDir;

    public DeviceProfileRepository(string? customProfilesDir = null)
    {
        _customProfilesDir = customProfilesDir;
        foreach (var seed in BuildBuiltInReferenceProfiles())
        {
            _profiles[seed.ProfileId] = seed;
        }

        var candidateDirs = new List<string>();
        var bundledProfilesDir = Path.Combine(AppContext.BaseDirectory, "profiles");
        if (Directory.Exists(bundledProfilesDir))
        {
            candidateDirs.Add(bundledProfilesDir);
        }
        if (!string.IsNullOrWhiteSpace(_customProfilesDir) && Directory.Exists(_customProfilesDir))
        {
            candidateDirs.Add(_customProfilesDir);
        }

        foreach (var dir in candidateDirs)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file, Encoding.UTF8);
                    var parsed = JsonSerializer.Deserialize<DeviceProfile>(json, JsonOpts);
                    if (parsed is not null && ValidateProfile(parsed).Count == 0)
                    {
                        _profiles[parsed.ProfileId] = parsed;
                    }
                }
                catch
                {
                    // Invalid external profile files are ignored during scan and reported on explicit import
                }
            }
        }
    }

    public IReadOnlyList<DeviceProfile> GetAllProfiles()
    {
        lock (_gate)
        {
            return _profiles.Values
                .OrderBy(p => p.Manufacturer)
                .ThenBy(p => p.MarketingName)
                .ToList();
        }
    }

    public DeviceProfile? GetById(string profileId)
    {
        lock (_gate)
        {
            return _profiles.TryGetValue(profileId, out var profile) ? profile : null;
        }
    }

    /// <summary>
    /// Matches a profile ONLY when manufacturer + model code, codename, or build fingerprint align.
    /// Explicitly refuses to match on chipset alone.
    /// </summary>
    public DeviceProfile? MatchVerifiedProfile(
        string? manufacturer,
        string? modelCode,
        string? codename,
        string? buildFingerprint)
    {
        lock (_gate)
        {
            foreach (var profile in _profiles.Values)
            {
                var mfgMatches = string.IsNullOrWhiteSpace(manufacturer) ||
                    manufacturer.Contains(profile.Manufacturer, StringComparison.OrdinalIgnoreCase);

                if (!mfgMatches)
                {
                    continue;
                }

                // 1. Fingerprint match (strongest: e.g. TECNO/CL9-OP/TECNO-CL9:15/...)
                if (!string.IsNullOrWhiteSpace(buildFingerprint))
                {
                    var fp = buildFingerprint.Trim();
                    if (fp.StartsWith($"{profile.Manufacturer}/", StringComparison.OrdinalIgnoreCase) &&
                        (fp.Contains($"/{profile.Codename}:", StringComparison.OrdinalIgnoreCase) ||
                         profile.ModelCodes.Any(mc => fp.Contains(mc, StringComparison.OrdinalIgnoreCase))))
                    {
                        return profile;
                    }
                }

                // 2. Exact model code match (e.g. "TECNO CL6" or "CL6" or "Infinix X6850")
                if (!string.IsNullOrWhiteSpace(modelCode))
                {
                    var normalizedModel = modelCode.Trim();
                    foreach (var code in profile.ModelCodes)
                    {
                        if (string.Equals(normalizedModel, code, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(normalizedModel, $"{profile.Manufacturer} {code}", StringComparison.OrdinalIgnoreCase))
                        {
                            return profile;
                        }
                    }
                }

                // 3. Exact codename match when manufacturer is explicitly known
                if (!string.IsNullOrWhiteSpace(manufacturer) &&
                    !string.IsNullOrWhiteSpace(codename) &&
                    string.Equals(codename.Trim(), profile.Codename, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }

            return null;
        }
    }

    public async Task<DeviceProfile> ImportProfileFromFileAsync(string jsonFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(jsonFilePath))
        {
            throw new ServicingException("PROFILE_NOT_FOUND", $"Profile file does not exist: {jsonFilePath}");
        }

        var json = await File.ReadAllTextAsync(jsonFilePath, Encoding.UTF8, ct).ConfigureAwait(false);
        var profile = JsonSerializer.Deserialize<DeviceProfile>(json, JsonOpts)
            ?? throw new ServicingException("PROFILE_INVALID_JSON", "Failed to deserialize device profile JSON.");

        var errors = ValidateProfile(profile);
        if (errors.Count > 0)
        {
            throw new ServicingException("PROFILE_SCHEMA_INVALID", "Profile validation failed: " + string.Join("; ", errors));
        }

        lock (_gate)
        {
            _profiles[profile.ProfileId] = profile;
        }

        if (!string.IsNullOrWhiteSpace(_customProfilesDir))
        {
            Directory.CreateDirectory(_customProfilesDir);
            var targetPath = Path.Combine(_customProfilesDir, $"{profile.ProfileId}.json");
            var normalizedJson = JsonSerializer.Serialize(profile, JsonOpts);
            await File.WriteAllTextAsync(targetPath, normalizedJson, Encoding.UTF8, ct).ConfigureAwait(false);
        }

        return profile;
    }

    public async Task<DeviceProfile> SaveOrPromoteProfileAsync(DeviceProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = ValidateProfile(profile);
        if (errors.Count > 0)
        {
            throw new ServicingException("PROFILE_SCHEMA_INVALID", "Profile validation failed: " + string.Join("; ", errors));
        }

        lock (_gate)
        {
            _profiles[profile.ProfileId] = profile;
        }

        if (!string.IsNullOrWhiteSpace(_customProfilesDir))
        {
            Directory.CreateDirectory(_customProfilesDir);
            var targetPath = Path.Combine(_customProfilesDir, $"{profile.ProfileId}.json");
            var normalizedJson = JsonSerializer.Serialize(profile, JsonOpts);
            await File.WriteAllTextAsync(targetPath, normalizedJson, Encoding.UTF8, ct).ConfigureAwait(false);
        }

        return profile;
    }

    public IReadOnlyList<string> ValidateProfile(DeviceProfile profile)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.ProfileId))
            errors.Add("ProfileId is required.");
        if (string.IsNullOrWhiteSpace(profile.Manufacturer))
            errors.Add("Manufacturer is required (e.g. TECNO, Infinix, itel).");
        if (string.IsNullOrWhiteSpace(profile.MarketingName))
            errors.Add("MarketingName is required.");
        if (profile.ModelCodes is null || profile.ModelCodes.Count == 0)
            errors.Add("At least one exact ModelCode is required.");
        if (string.IsNullOrWhiteSpace(profile.Codename))
            errors.Add("Codename is required.");
        if (profile.ChipsetVendor == ChipsetVendor.Unknown || string.IsNullOrWhiteSpace(profile.ChipsetCode))
            errors.Add("ChipsetVendor and ChipsetCode are required.");
        if (profile.MinAndroidVersion < 5 || profile.MaxVerifiedAndroidVersion > 16 || profile.MinAndroidVersion > profile.MaxVerifiedAndroidVersion)
            errors.Add("Android version bounds must be between Android 5 and Android 16 with Min <= Max.");
        if (profile.ProtectedPartitions is null || profile.ProtectedPartitions.Count == 0)
            errors.Add("ProtectedPartitions must declare NVRAM/calibration partitions to protect.");
        if (profile.VerificationLevel == HardwareVerificationLevel.HardwareVerified && !profile.HardwareVerification.IsHardwareVerified)
            errors.Add("VerificationLevel cannot be HardwareVerified when HardwareVerification.IsHardwareVerified is false.");
        if (profile.HardwareVerification.IsHardwareVerified && string.IsNullOrWhiteSpace(profile.HardwareVerification.TestedBuildFingerprint))
            errors.Add("Hardware-verified profiles must include TestedBuildFingerprint.");

        return errors;
    }

    public static string SerializeProfile(DeviceProfile profile) =>
        JsonSerializer.Serialize(profile, JsonOpts);

    private static List<DeviceProfile> BuildBuiltInReferenceProfiles()
    {
        string[] mtkProtected = ["nvram", "nvdata", "nvcfg", "persist", "proinfo", "protect1", "protect2", "seccfg", "otp", "preloader"];
        string[] unisocProtected = ["prodnv", "miscdata", "persist", "PhaseCheck", "l_fixnv1", "l_fixnv2", "l_runtimenv1", "splloader"];

        PartitionRuleSpec[] mtkVabPartitions =
        [
            new("boot_a", false, false, true, 67_108_864),
            new("boot_b", false, false, true, 67_108_864),
            new("init_boot_a", false, false, true, 8_388_608),
            new("init_boot_b", false, false, true, 8_388_608),
            new("vendor_boot_a", false, false, true, 67_108_864),
            new("vendor_boot_b", false, false, true, 67_108_864),
            new("vbmeta_a", false, false, true, 8_388_608),
            new("vbmeta_b", false, false, true, 8_388_608),
            new("dtbo_a", false, false, false, 33_554_432),
            new("super", false, false, true, 9_663_676_416),
            new("system_a", true, false, true, 5_368_709_120),
            new("vendor_a", true, false, true, 1_610_612_736),
            new("product_a", true, false, true, 2_684_354_560),
            new("nvram", false, true, false, 67_108_864),
            new("nvdata", false, true, false, 67_108_864),
            new("persist", false, true, false, 67_108_864)
        ];

        PartitionRuleSpec[] unisocAbPartitions =
        [
            new("boot_a", false, false, true, 67_108_864),
            new("boot_b", false, false, true, 67_108_864),
            new("vendor_boot_a", false, false, true, 67_108_864),
            new("vbmeta_a", false, false, true, 1_048_576),
            new("vbmeta_system_a", false, false, true, 1_048_576),
            new("super", false, false, true, 8_589_934_592),
            new("system_a", true, false, true, 4_294_967_296),
            new("product_a", true, false, true, 2_147_483_648),
            new("prodnv", false, true, false, 10_485_760),
            new("miscdata", false, true, false, 1_048_576)
        ];

        return
        [
            new DeviceProfile
            {
                ProfileId = "tecno-camon30-premier-cl9",
                Manufacturer = "TECNO",
                MarketingName = "TECNO CAMON 30 Premier 5G",
                ModelCodes = ["CL9", "TECNO CL9"],
                Codename = "TECNO-CL9",
                UiSkin = "HiOS 14 / 15 / 16",
                ChipsetVendor = ChipsetVendor.MediaTek,
                ChipsetFamily = "MediaTek Dimensity 8200 Ultimate",
                ChipsetCode = "MT6896",
                StorageType = "UFS 3.1",
                PartitionScheme = "Virtual A/B Dynamic (GKI 2.0 init_boot)",
                MinAndroidVersion = 14,
                MaxVerifiedAndroidVersion = 16,
                Android16NotesApplicable = true,
                Android16CompatibilityNote = "Uses GKI 2.0 split boot/init_boot architecture and Virtual A/B v3 snapshots; verify pending OTA merge state via snapshotctl before bootloader unlock or fastbootd resize.",
                BromSlaDaaEnforced = true,
                UnisocSignedFdlEnforced = false,
                BootloaderPolicy = new BootloaderUnlockSpec(
                    OemUnlockToggleSupported: true,
                    RequiresFastbootFlashingUnlock: true,
                    UsesLegacyOemUnlock: false,
                    RequiresManufacturerToken: false,
                    SupportsFastbootdLogicalFlashing: true,
                    RequiresAvbVbmetaDisableConfirmation: true,
                    UnlockCommand: "fastboot flashing unlock",
                    Prerequisites:
                    [
                        "Enable Developer Options and toggle ON 'OEM unlocking' in HiOS Settings.",
                        "Ensure no pending Virtual A/B OTA merge is incomplete.",
                        "Battery charge >= 50% and signed Google/WinUSB fastboot interface installed."
                    ],
                    DeviceSpecificSteps:
                    [
                        "1. Connect over authorized ADB and run 'Reboot to Bootloader' (or hold Vol- while powering on).",
                        "2. Confirm fastboot enumerates as 'TECNO-CL9' in bootloader mode (is-userspace: no).",
                        "3. Issue 'fastboot flashing unlock' and press Volume Up on the handset prompt within 15 seconds.",
                        "4. Allow mandatory userdata cryptographic wipe to complete before rebooting."
                    ],
                    DataLossAndSecurityWarnings:
                    [
                        "Unlocking erases ALL user data (photos, messages, credentials) via hardware key destruction.",
                        "AVB Verified Boot transitions to ORANGE state with boot warning banner.",
                        "Widevine DRM degrades from L1 to L3 and Play Integrity strong attestation will fail."
                    ]),
                ProtectedPartitions = mtkProtected,
                KnownPartitions = mtkVabPartitions,
                SupportedModes =
                [
                    ConnectionMode.AdbAuthorized, ConnectionMode.Recovery, ConnectionMode.AdbSideload,
                    ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace,
                    ConnectionMode.MediaTekPreloader, ConnectionMode.MediaTekBrom, ConnectionMode.MediaTekMeta
                ],
                VerificationLevel = HardwareVerificationLevel.ReferenceSpecification,
                HardwareVerification = new HardwareVerificationRecord(
                    IsHardwareVerified: false,
                    TestedBy: null,
                    TestedAtUtc: null,
                    TestedBuildFingerprint: null,
                    VerificationNotes: "Reference engineering specification based on documented MT6896 GKI 2.0 / HiOS partition layout. Attach bench verification record to promote to HardwareVerified.")
            },
            new DeviceProfile
            {
                ProfileId = "tecno-camon30-4g-cl6",
                Manufacturer = "TECNO",
                MarketingName = "TECNO CAMON 30 4G",
                ModelCodes = ["CL6", "CL6k", "TECNO CL6"],
                Codename = "TECNO-CL6",
                UiSkin = "HiOS 14 / 15",
                ChipsetVendor = ChipsetVendor.MediaTek,
                ChipsetFamily = "MediaTek Helio G99 Ultimate",
                ChipsetCode = "MT6789",
                StorageType = "UFS 2.2",
                PartitionScheme = "Virtual A/B Dynamic",
                MinAndroidVersion = 14,
                MaxVerifiedAndroidVersion = 15,
                Android16NotesApplicable = false,
                Android16CompatibilityNote = "Android 16 bootloader compatibility must be verified per regional build before flashing.",
                BromSlaDaaEnforced = true,
                UnisocSignedFdlEnforced = false,
                BootloaderPolicy = new BootloaderUnlockSpec(
                    OemUnlockToggleSupported: true,
                    RequiresFastbootFlashingUnlock: true,
                    UsesLegacyOemUnlock: false,
                    RequiresManufacturerToken: false,
                    SupportsFastbootdLogicalFlashing: true,
                    RequiresAvbVbmetaDisableConfirmation: true,
                    UnlockCommand: "fastboot flashing unlock",
                    Prerequisites:
                    [
                        "Enable 'OEM unlocking' and 'USB debugging' in HiOS Developer Options.",
                        "Install signed WinUSB fastboot driver if Windows shows Android Bootloader Interface with Code 28."
                    ],
                    DeviceSpecificSteps:
                    [
                        "1. Reboot to Bootloader via ADB ('adb reboot bootloader').",
                        "2. Verify 'fastboot getvar unlocked' currently reports 'no'.",
                        "3. Run 'fastboot flashing unlock' and confirm with Volume Up on the device.",
                        "4. Verify resulting state via 'fastboot getvar unlocked' -> 'yes'."
                    ],
                    DataLossAndSecurityWarnings:
                    [
                        "Mandatory factory reset wipes all internal storage.",
                        "BROM/Preloader SLA+DAA remains hardware-enforced; unauthenticated BROM flashing is not supported."
                    ]),
                ProtectedPartitions = mtkProtected,
                KnownPartitions = mtkVabPartitions,
                SupportedModes =
                [
                    ConnectionMode.AdbAuthorized, ConnectionMode.Recovery, ConnectionMode.AdbSideload,
                    ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace,
                    ConnectionMode.MediaTekPreloader, ConnectionMode.MediaTekBrom, ConnectionMode.MediaTekMeta
                ],
                VerificationLevel = HardwareVerificationLevel.ReferenceSpecification,
                HardwareVerification = new HardwareVerificationRecord(false, null, null, null, "Documented reference profile; physical bench verification pending.")
            },
            new DeviceProfile
            {
                ProfileId = "tecno-pop8-bg6",
                Manufacturer = "TECNO",
                MarketingName = "TECNO POP 8",
                ModelCodes = ["BG6", "BG6i", "TECNO BG6"],
                Codename = "TECNO-BG6",
                UiSkin = "HiOS 13 Go",
                ChipsetVendor = ChipsetVendor.Unisoc,
                ChipsetFamily = "Unisoc Tiger T606",
                ChipsetCode = "UMS9230",
                StorageType = "UFS 2.2 / eMMC 5.1",
                PartitionScheme = "A/B Dynamic",
                MinAndroidVersion = 13,
                MaxVerifiedAndroidVersion = 14,
                Android16NotesApplicable = false,
                Android16CompatibilityNote = null,
                BromSlaDaaEnforced = false,
                UnisocSignedFdlEnforced = true,
                BootloaderPolicy = new BootloaderUnlockSpec(
                    OemUnlockToggleSupported: true,
                    RequiresFastbootFlashingUnlock: false,
                    UsesLegacyOemUnlock: false,
                    RequiresManufacturerToken: true,
                    SupportsFastbootdLogicalFlashing: true,
                    RequiresAvbVbmetaDisableConfirmation: true,
                    UnlockCommand: "fastboot flashing unlock (restricted on Unisoc Secure Boot without OEM signature token)",
                    Prerequisites:
                    [
                        "Unisoc UMS9230 bootloaders typically enforce RSA identifier token signing ('fastboot oem get_identifier_token').",
                        "Standard unauthenticated 'fastboot flashing unlock' is commonly rejected by Unisoc U-Boot unless signed token is supplied."
                    ],
                    DeviceSpecificSteps:
                    [
                        "1. Enter fastbootd or bootloader to inspect 'fastboot oem get_identifier_token' availability.",
                        "2. Do NOT use third-party modified FDL exploits; use official signed PAC packages in ResearchDownload when authorized."
                    ],
                    DataLossAndSecurityWarnings:
                    [
                        "Modifying prodnv or l_fixnv1/2 permanently corrupts IMEI/RF calibration.",
                        "Unisoc Download Mode requires vendor-signed fdl1-sign.bin and fdl2-sign.bin."
                    ]),
                ProtectedPartitions = unisocProtected,
                KnownPartitions = unisocAbPartitions,
                SupportedModes =
                [
                    ConnectionMode.AdbAuthorized, ConnectionMode.Recovery, ConnectionMode.AdbSideload,
                    ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace,
                    ConnectionMode.UnisocDownload, ConnectionMode.UnisocDiag
                ],
                VerificationLevel = HardwareVerificationLevel.ReferenceSpecification,
                HardwareVerification = new HardwareVerificationRecord(false, null, null, null, "Documented Unisoc T606 reference profile.")
            },
            new DeviceProfile
            {
                ProfileId = "infinix-gt20-pro-x6871",
                Manufacturer = "Infinix",
                MarketingName = "Infinix GT 20 Pro 5G",
                ModelCodes = ["X6871", "Infinix X6871"],
                Codename = "Infinix-X6871",
                UiSkin = "XOS 14 / 15 / 16 for GT",
                ChipsetVendor = ChipsetVendor.MediaTek,
                ChipsetFamily = "MediaTek Dimensity 8200 Ultimate",
                ChipsetCode = "MT6896",
                StorageType = "UFS 3.1",
                PartitionScheme = "Virtual A/B Dynamic (GKI 2.0)",
                MinAndroidVersion = 14,
                MaxVerifiedAndroidVersion = 16,
                Android16NotesApplicable = true,
                Android16CompatibilityNote = "Supports Android 15/16 GKI 2.0 layout; ramdisk resides in init_boot_a/b rather than boot_a/b.",
                BromSlaDaaEnforced = true,
                UnisocSignedFdlEnforced = false,
                BootloaderPolicy = new BootloaderUnlockSpec(
                    OemUnlockToggleSupported: true,
                    RequiresFastbootFlashingUnlock: true,
                    UsesLegacyOemUnlock: false,
                    RequiresManufacturerToken: false,
                    SupportsFastbootdLogicalFlashing: true,
                    RequiresAvbVbmetaDisableConfirmation: true,
                    UnlockCommand: "fastboot flashing unlock",
                    Prerequisites:
                    [
                        "Enable 'OEM unlocking' in XOS Developer Options.",
                        "Ensure Virtual A/B snapshot merge status is 'none' before unlocking."
                    ],
                    DeviceSpecificSteps:
                    [
                        "1. Reboot to Bootloader via 'adb reboot bootloader'.",
                        "2. Execute 'fastboot flashing unlock' and confirm via Volume Up key.",
                        "3. Re-query 'fastboot getvar unlocked' to verify unlock completion."
                    ],
                    DataLossAndSecurityWarnings:
                    [
                        "Erases all userdata immediately upon confirmation.",
                        "Kernel modifications on GKI 2.0 require matching KMI version; ramdisk changes belong in init_boot."
                    ]),
                ProtectedPartitions = mtkProtected,
                KnownPartitions = mtkVabPartitions,
                SupportedModes =
                [
                    ConnectionMode.AdbAuthorized, ConnectionMode.Recovery, ConnectionMode.AdbSideload,
                    ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace,
                    ConnectionMode.MediaTekPreloader, ConnectionMode.MediaTekBrom, ConnectionMode.MediaTekMeta
                ],
                VerificationLevel = HardwareVerificationLevel.ReferenceSpecification,
                HardwareVerification = new HardwareVerificationRecord(false, null, null, null, "Documented XOS / MT6896 reference profile.")
            },
            new DeviceProfile
            {
                ProfileId = "infinix-note40-pro-x6850",
                Manufacturer = "Infinix",
                MarketingName = "Infinix NOTE 40 Pro",
                ModelCodes = ["X6850", "Infinix X6850"],
                Codename = "Infinix-X6850",
                UiSkin = "XOS 14 / 15",
                ChipsetVendor = ChipsetVendor.MediaTek,
                ChipsetFamily = "MediaTek Helio G99 Ultimate",
                ChipsetCode = "MT6789",
                StorageType = "UFS 2.2",
                PartitionScheme = "Virtual A/B Dynamic",
                MinAndroidVersion = 14,
                MaxVerifiedAndroidVersion = 16,
                Android16NotesApplicable = true,
                Android16CompatibilityNote = "Verify XOS Android 16 OTA bootloader anti-rollback index before flashing older stock boot/vbmeta images.",
                BromSlaDaaEnforced = true,
                UnisocSignedFdlEnforced = false,
                BootloaderPolicy = new BootloaderUnlockSpec(
                    OemUnlockToggleSupported: true,
                    RequiresFastbootFlashingUnlock: true,
                    UsesLegacyOemUnlock: false,
                    RequiresManufacturerToken: false,
                    SupportsFastbootdLogicalFlashing: true,
                    RequiresAvbVbmetaDisableConfirmation: true,
                    UnlockCommand: "fastboot flashing unlock",
                    Prerequisites:
                    [
                        "Enable 'OEM unlocking' in XOS Settings -> My Phone -> Build Number (7x) -> Developer Options."
                    ],
                    DeviceSpecificSteps:
                    [
                        "1. Enter bootloader via 'adb reboot bootloader'.",
                        "2. Run 'fastboot flashing unlock' and press Volume Up on device.",
                        "3. Confirm 'unlocked: yes' via fastboot getvar."
                    ],
                    DataLossAndSecurityWarnings:
                    [
                        "All user data will be wiped; never flash lower anti-rollback vbmeta."
                    ]),
                ProtectedPartitions = mtkProtected,
                KnownPartitions = mtkVabPartitions,
                SupportedModes =
                [
                    ConnectionMode.AdbAuthorized, ConnectionMode.Recovery, ConnectionMode.AdbSideload,
                    ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace,
                    ConnectionMode.MediaTekPreloader, ConnectionMode.MediaTekBrom, ConnectionMode.MediaTekMeta
                ],
                VerificationLevel = HardwareVerificationLevel.ReferenceSpecification,
                HardwareVerification = new HardwareVerificationRecord(false, null, null, null, "Documented XOS / MT6789 reference profile.")
            },
            new DeviceProfile
            {
                ProfileId = "infinix-smart8-x6525",
                Manufacturer = "Infinix",
                MarketingName = "Infinix SMART 8",
                ModelCodes = ["X6525", "X6525B", "Infinix X6525"],
                Codename = "Infinix-X6525",
                UiSkin = "XOS 13",
                ChipsetVendor = ChipsetVendor.Unisoc,
                ChipsetFamily = "Unisoc Tiger T606",
                ChipsetCode = "UMS9230",
                StorageType = "UFS 2.2 / eMMC 5.1",
                PartitionScheme = "A/B Dynamic",
                MinAndroidVersion = 13,
                MaxVerifiedAndroidVersion = 14,
                Android16NotesApplicable = false,
                Android16CompatibilityNote = null,
                BromSlaDaaEnforced = false,
                UnisocSignedFdlEnforced = true,
                BootloaderPolicy = new BootloaderUnlockSpec(
                    OemUnlockToggleSupported: true,
                    RequiresFastbootFlashingUnlock: false,
                    UsesLegacyOemUnlock: false,
                    RequiresManufacturerToken: true,
                    SupportsFastbootdLogicalFlashing: true,
                    RequiresAvbVbmetaDisableConfirmation: true,
                    UnlockCommand: "Requires signed Unisoc identifier token",
                    Prerequisites:
                    [
                        "Unisoc Secure Boot enforces RSA token verification for bootloader unlock."
                    ],
                    DeviceSpecificSteps:
                    [
                        "1. Inspect Unisoc PAC firmware via Firmware Explorer to verify signed FDL1/FDL2 presence.",
                        "2. Use authorized OTA sideload or signed ResearchDownload workflow for stock restoration."
                    ],
                    DataLossAndSecurityWarnings:
                    [
                        "Never overwrite prodnv or calibration partitions."
                    ]),
                ProtectedPartitions = unisocProtected,
                KnownPartitions = unisocAbPartitions,
                SupportedModes =
                [
                    ConnectionMode.AdbAuthorized, ConnectionMode.Recovery, ConnectionMode.AdbSideload,
                    ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace,
                    ConnectionMode.UnisocDownload, ConnectionMode.UnisocDiag
                ],
                VerificationLevel = HardwareVerificationLevel.ReferenceSpecification,
                HardwareVerification = new HardwareVerificationRecord(false, null, null, null, "Documented Unisoc T606 reference profile.")
            },
            new DeviceProfile
            {
                ProfileId = "itel-s24-s667ln",
                Manufacturer = "itel",
                MarketingName = "itel S24",
                ModelCodes = ["S667LN", "S667L", "itel S667LN"],
                Codename = "itel-S667LN",
                UiSkin = "itelOS 13.5 / 14",
                ChipsetVendor = ChipsetVendor.MediaTek,
                ChipsetFamily = "MediaTek Helio G91 Ultra",
                ChipsetCode = "MT6769Z",
                StorageType = "eMMC 5.1",
                PartitionScheme = "Virtual A/B Dynamic",
                MinAndroidVersion = 13,
                MaxVerifiedAndroidVersion = 14,
                Android16NotesApplicable = false,
                Android16CompatibilityNote = null,
                BromSlaDaaEnforced = true,
                UnisocSignedFdlEnforced = false,
                BootloaderPolicy = new BootloaderUnlockSpec(
                    OemUnlockToggleSupported: true,
                    RequiresFastbootFlashingUnlock: true,
                    UsesLegacyOemUnlock: false,
                    RequiresManufacturerToken: false,
                    SupportsFastbootdLogicalFlashing: true,
                    RequiresAvbVbmetaDisableConfirmation: true,
                    UnlockCommand: "fastboot flashing unlock",
                    Prerequisites:
                    [
                        "Enable 'OEM unlocking' in itelOS Developer Options.",
                        "Confirm fastboot USB driver is bound to Android Bootloader Interface."
                    ],
                    DeviceSpecificSteps:
                    [
                        "1. Reboot to bootloader via 'adb reboot bootloader'.",
                        "2. Execute 'fastboot flashing unlock' and confirm with Volume Up.",
                        "3. Re-query 'fastboot getvar unlocked' to confirm state."
                    ],
                    DataLossAndSecurityWarnings:
                    [
                        "Wipes all user data; SLA/DAA restricts unauthenticated BROM writes."
                    ]),
                ProtectedPartitions = mtkProtected,
                KnownPartitions = mtkVabPartitions,
                SupportedModes =
                [
                    ConnectionMode.AdbAuthorized, ConnectionMode.Recovery, ConnectionMode.AdbSideload,
                    ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace,
                    ConnectionMode.MediaTekPreloader, ConnectionMode.MediaTekBrom, ConnectionMode.MediaTekMeta
                ],
                VerificationLevel = HardwareVerificationLevel.ReferenceSpecification,
                HardwareVerification = new HardwareVerificationRecord(false, null, null, null, "Documented itelOS / MT6769Z reference profile.")
            },
            new DeviceProfile
            {
                ProfileId = "itel-p55-5g-p661n",
                Manufacturer = "itel",
                MarketingName = "itel P55 5G",
                ModelCodes = ["P661N", "itel P661N"],
                Codename = "itel-P661N",
                UiSkin = "itelOS 13",
                ChipsetVendor = ChipsetVendor.MediaTek,
                ChipsetFamily = "MediaTek Dimensity 6080",
                ChipsetCode = "MT6833P",
                StorageType = "UFS 2.2",
                PartitionScheme = "Virtual A/B Dynamic",
                MinAndroidVersion = 13,
                MaxVerifiedAndroidVersion = 15,
                Android16NotesApplicable = false,
                Android16CompatibilityNote = null,
                BromSlaDaaEnforced = true,
                UnisocSignedFdlEnforced = false,
                BootloaderPolicy = new BootloaderUnlockSpec(
                    OemUnlockToggleSupported: true,
                    RequiresFastbootFlashingUnlock: true,
                    UsesLegacyOemUnlock: false,
                    RequiresManufacturerToken: false,
                    SupportsFastbootdLogicalFlashing: true,
                    RequiresAvbVbmetaDisableConfirmation: true,
                    UnlockCommand: "fastboot flashing unlock",
                    Prerequisites: ["Enable 'OEM unlocking' in itelOS Developer Options."],
                    DeviceSpecificSteps:
                    [
                        "1. Enter bootloader via 'adb reboot bootloader'.",
                        "2. Issue 'fastboot flashing unlock' and confirm on device screen.",
                        "3. Verify 'unlocked: yes' via fastboot getvar."
                    ],
                    DataLossAndSecurityWarnings: ["Triggers full userdata factory reset."]),
                ProtectedPartitions = mtkProtected,
                KnownPartitions = mtkVabPartitions,
                SupportedModes =
                [
                    ConnectionMode.AdbAuthorized, ConnectionMode.Recovery, ConnectionMode.AdbSideload,
                    ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace,
                    ConnectionMode.MediaTekPreloader, ConnectionMode.MediaTekBrom, ConnectionMode.MediaTekMeta
                ],
                VerificationLevel = HardwareVerificationLevel.ReferenceSpecification,
                HardwareVerification = new HardwareVerificationRecord(false, null, null, null, "Documented itel P55 5G reference profile.")
            }
        ];
    }
}
