# Feature & Protocol Support Matrix

This document defines the exact implementation, automated test coverage, physical hardware verification state, and explicit capability boundaries of **TranssionLab v1.0.0**.

> **Engineering Honesty Policy (Section 11 & 13):**
> TranssionLab never labels a protocol provider as *Hardware-Verified* merely because its interface, parser, or unit test exists. Capabilities below are marked `Hardware-Verified: Pending Bench Sign-Off` until tested against a physical handset and recorded in a signed `HardwareVerificationRecord`.

---

## 1. Protocol & Connection Subsystem Matrix

| Subsystem / Mode | Provider Class | Implementation Status | Automated Unit Tests | Hardware-Verified | Supported Scope & Explicit Limitations |
| :--- | :--- | :--- | :---: | :---: | :--- |
| **USB & Windows PnP Discovery** | `UsbInterfaceProvider` | **Implemented** (`Win32_PnPEntity` WMI + arrival/removal watchers + VID/PID/MI catalog) | **Yes** (`SimulatedPnpBackend`) | Pending Windows Bench | Detects `0x2ECC` (Transsion), `0x18D1` (Google), `0x0E8D` (MediaTek), `0x0BB4` (MTK LK Fastboot), `0x1782` (Unisoc), `0x05C6` (Qualcomm); maps COM ports; diagnoses PnP Codes 0/10/22/28/43/52. Never installs unsigned drivers or disables Windows Driver Signature Enforcement. |
| **ADB (USB)** | `AdbProvider` | **Implemented** (`adb devices -l`, `getprop`, `dumpsys battery`, `df -h`, `reboot`) | **Yes** | Pending Windows Bench | Distinguishes `Connected`, `Unauthorized`, `Authorizing`, `Offline`, `Recovery`, `Sideload`, and `Unavailable`. Requires host RSA authorization on handset screen. |
| **Recovery & Sideload** | `RecoveryProvider` | **Implemented** (`adb sideload` with ZIP header check) | **Yes** | Pending Windows Bench | Streams OEM-signed OTA `.zip` packages. Device recovery enforces OEM RSA/AVB cryptographic verification and anti-rollback counters. |
| **Fastboot & Fastbootd** | `FastbootProvider` | **Implemented** (`is-userspace` detection, `getvar`, `set_active`, `flash`, `flashing unlock`) | **Yes** | Pending Windows Bench | Separates primary Bootloader Fastboot (`is-userspace: no`) from Userspace Fastbootd (`is-userspace: yes`). Logical dynamic partitions inside `super` require Fastbootd. |
| **MediaTek BROM / Preloader / DA** | `MediaTekProvider` | **Read-Only Diagnostics Implemented** (4-byte sync `A0 0A 50 05`, `0xFD`, `0xFC`, `0xD8`) | **Yes** | Pending Windows Bench | Reads SoC `HW_CODE` and `SBC`/`SLA`/`DAA` security flags; inspects Text & XML scatter packages. **Unsupported by Policy:** BROM exploits, SLA/DAA bypasses, or unauthenticated DA uploads. |
| **MediaTek META Mode** | `MetaModeProvider` | **USB Detection & Provider Readiness Implemented** | **Yes** | Pending Windows Bench | Identifies `0E8D:2006/2007/200A/200D` META endpoints and checks local `MetaCore.dll`/`SPMETA_DLL.dll` availability. Never fabricates META sessions on generic USB endpoints; proprietary ModemMETA libraries are not bundled. |
| **Spreadtrum / Unisoc** | `SpdUnisocProvider` | **Read-Only Boot Probe & PAC Container Implemented** | **Yes** | Pending Windows Bench | Implements HDLC `0x7E` framing, `0x7D` byte-stuffing, CRC-16 XMODEM, `BSL_REP_VER (0x81)` banner probe, and binary `.pac` inspection/extraction. Flashing on Secure Boot Unisoc SoCs (`T603`/`T606`/`T612`/`T616`) requires OEM-signed `FDL1`/`FDL2`. |
| **DIAG Interfaces** | `DiagnosticProvider` | **Chipset Classification & Read-Only Qualcomm Version Probe** | **Yes** | Pending Windows Bench | Distinguishes Qualcomm QCDM (`0x05C6`), Unisoc DIAG (`0x1782`), and MediaTek ELT. Blocks sending Qualcomm QCDM commands to MediaTek/Unisoc devices; blocks all NV/IMEI/radio writes. |

---

## 2. Servicing, Firmware & Application Feature Matrix

| Feature Area | Status | Automated Unit Tests | Key Safety & Architectural Guarantees |
| :--- | :--- | :---: | :--- |
| **Explicit Multi-Device Selection** | **Implemented** | **Yes** | Never silently selects a phone when >= 2 devices are connected; throws `AmbiguousDeviceSelectionException` and displays mandatory UI warning banner. |
| **Data Provenance Tracking** | **Implemented** | **Yes** | Every identity attribute carries `DETECTED`, `INFERRED`, `VERIFIED`, or `UNKNOWN`. Never infers exact phone model from SoC/chipset alone. |
| **Bootloader Manager (Android 8–16)** | **Implemented** | **Yes** | Checks `sys.oem_unlock_allowed`, `get_unlock_ability`, and profile policy; enforces exact destructive confirmation phrase; verifies actual post-command `unlocked` state; documents Android 16 GKI 2.0 / Virtual A/B caveats. |
| **Firmware Explorer** | **Implemented** | **Yes** | Offline binary inspection & extraction for `.zip`, Signed OTA `.zip`, `.tar`/`.tgz`, standalone & embedded `payload.bin` (`CrAU` v2 `REPLACE`/`REPLACE_BZ`/`REPLACE_XZ`/`ZSTD`/`ZERO` extractor), MTK Scatter (Text & XML), Unisoc `.pac`, Sparse `.img` (`0xED26FF3A` + Sparse-to-Raw converter), Dynamic `super.img` (`LpMetadata`), Boot/Vendor_Boot, `AVB0` vbmeta, and raw ext4/EROFS/F2FS images. |
| **Partition Manager** | **Implemented** | **Yes** | Enumerates live GPT & Dynamic `super` partitions across Fastboot, Fastbootd, ADB, and Recovery; creates/resizes/deletes logical `super` sub-partitions in unlocked Fastbootd; runs controlled erase (`cache`/`metadata`/explicit `userdata`, permanently blocking FRP/NVRAM/bootloader); reads back unprotected `.img` partitions in Recovery/root ADB with SHA-256; flashes verified `AVB0` `vbmeta`. |
| **ROM Studio** | **Implemented** | **Yes** | Extracts unprotected stock partition images from OTA `payload.bin`/ZIP/TAR/PAC/Scatter, converts Sparse to Raw `.img`, and validates Linux/WSL2 + 350 GiB + AOSP/device-tree/kernel/vendor prerequisites for custom ROM builds. |
| **11-Step Flash Manager** | **Implemented** | **Yes** | Enforces device ID, verified profile, Fastboot vs Fastbootd mode, SHA-256 integrity, partition capacity fit, protected NVRAM/radio blocklist, mandatory Dry-Run, explicit confirmation token, post-state verification, and JSON audit report. Never auto-wipes `userdata`. |
| **Structured Logging & Redaction** | **Implemented** | **Yes** | Scrubs private keys, bearer headers, and `--token`/`--password` secrets; `ClearDisplayOnly()` preserves session `.jsonl` archive; exports `.txt`/`.json` and local crash reports. |
| **18-Page Dark Fluent WPF UI** | **Implemented** | **Yes** (DI + VM tests) | Compiles natively for `win-x64` and `win-arm64`; runs `asInvoker` (no UAC elevation prompt on startup); 100% offline-first. |
| **Security Bypasses / FRP / IMEI Edits** | **Intentionally Unsupported** | Enforced by Policy | Security circumvention, BROM/SLA exploits, anti-theft/FRP bypass, credential extraction, and protected radio/IMEI tampering are permanently excluded. |
