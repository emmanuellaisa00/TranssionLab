# Adding Hardware-Verified TECNO, Infinix & itel Device Profiles

TranssionLab uses a strict JSON schema (`DeviceProfile`) to govern device identification, partition safety rules, and bootloader unlock procedures.

## 1. Rules Before Promoting a Profile to `HardwareVerified`

1. **Never infer marketing model from SoC code alone.** Multiple TECNO, Infinix, and itel handsets share `MT6789` (Helio G99), `MT6896` (Dimensity 8200), or `UMS9230` (Unisoc T606) with incompatible partition sizes, DTBOs, and panel drivers.
2. **Capture real outputs from a physical handset:**
   - Connect over authorized ADB -> Page #3 (**Device Explorer**) -> **Export Snapshot JSON** (`ro.product.manufacturer`, `ro.product.model`, `ro.product.device`, `ro.build.fingerprint`, `ro.boot.slot_suffix`).
   - Reboot into Bootloader (`adb reboot bootloader`) and record `fastboot getvar all`.
   - Reboot into Userspace Fastbootd (`fastboot reboot fastboot`) and record `is-userspace: yes` and logical `partition-size:<name>` values.
3. **Populate `ProtectedPartitions`:**
   - MediaTek devices MUST protect at minimum: `nvram`, `nvdata`, `nvcfg`, `persist`, `proinfo`, `protect1`, `protect2`, `seccfg`, `otp`, `preloader`.
   - Unisoc devices MUST protect at minimum: `prodnv`, `miscdata`, `persist`, `PhaseCheck`, `l_fixnv1`, `l_fixnv2`, `l_runtimenv1`, `splloader`.
4. **Hardware Verification Integrity Check:**
   - `DeviceProfileRepository.ValidateProfile(...)` rejects any JSON file where `"VerificationLevel": "HardwareVerified"` is set unless `"HardwareVerification": { "IsHardwareVerified": true, "TestedBuildFingerprint": "<non-empty>" }` is also provided.

---

## 2. Minimal Verified Profile JSON Example

Save as e.g. `tecno-cl6-bench-verified.json` and import via **Page #14 (Device Profiles -> Import Local Profile)** or drop into `%LOCALAPPDATA%\TranssionLab\profiles\`:

```json
{
  "ProfileId": "tecno-camon30-4g-cl6-verified",
  "Manufacturer": "TECNO",
  "MarketingName": "TECNO CAMON 30 4G",
  "ModelCodes": ["CL6", "TECNO CL6"],
  "Codename": "TECNO-CL6",
  "UiSkin": "HiOS 14",
  "ChipsetVendor": "MediaTek",
  "ChipsetFamily": "MediaTek Helio G99 Ultimate",
  "ChipsetCode": "MT6789",
  "StorageType": "UFS 2.2",
  "PartitionScheme": "Virtual A/B Dynamic",
  "MinAndroidVersion": 14,
  "MaxVerifiedAndroidVersion": 15,
  "Android16NotesApplicable": false,
  "Android16CompatibilityNote": null,
  "BromSlaDaaEnforced": true,
  "UnisocSignedFdlEnforced": false,
  "BootloaderPolicy": {
    "OemUnlockToggleSupported": true,
    "RequiresFastbootFlashingUnlock": true,
    "UsesLegacyOemUnlock": false,
    "RequiresManufacturerToken": false,
    "SupportsFastbootdLogicalFlashing": true,
    "RequiresAvbVbmetaDisableConfirmation": true,
    "UnlockCommand": "fastboot flashing unlock",
    "Prerequisites": [
      "Enable OEM unlocking and USB debugging in HiOS Developer Options."
    ],
    "DeviceSpecificSteps": [
      "1. Run 'adb reboot bootloader'.",
      "2. Execute 'fastboot flashing unlock' and press Volume Up on handset.",
      "3. Confirm 'fastboot getvar unlocked' returns 'yes'."
    ],
    "DataLossAndSecurityWarnings": [
      "Triggers mandatory cryptographic wipe of all userdata."
    ]
  },
  "ProtectedPartitions": [
    "nvram", "nvdata", "nvcfg", "persist", "proinfo", "protect1", "protect2", "seccfg", "otp", "preloader"
  ],
  "KnownPartitions": [
    {
      "PartitionName": "boot_a",
      "IsLogicalInSuper": false,
      "IsProtectedRadioOrNv": false,
      "IsBootCritical": true,
      "MaxExpectedBytes": 67108864
    },
    {
      "PartitionName": "system_a",
      "IsLogicalInSuper": true,
      "IsProtectedRadioOrNv": false,
      "IsBootCritical": true,
      "MaxExpectedBytes": 5368709120
    }
  ],
  "SupportedModes": [
    "AdbAuthorized", "Recovery", "AdbSideload", "FastbootBootloader", "FastbootdUserspace", "MediaTekPreloader", "MediaTekBrom"
  ],
  "VerificationLevel": "HardwareVerified",
  "HardwareVerification": {
    "IsHardwareVerified": true,
    "TestedBy": "Bench Engineer Name",
    "TestedAtUtc": "2026-10-24T12:00:00Z",
    "TestedBuildFingerprint": "TECNO/CL6-OP/TECNO-CL6:14/UP1A.231005.007/240915V120:user/release-keys",
    "VerificationNotes": "Verified ADB getprop, Fastbootd logical query, and boot_a dry-run capacity check on retail CL6 unit."
  }
}
```
