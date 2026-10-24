# Windows USB Driver Diagnostics & Third-Party Licensing

## 1. Windows USB Driver Requirements (WHQL-Signed Only)

TranssionLab inspects Windows Plug and Play (`Win32_PnPEntity`) error codes without requiring Administrator privileges on launch (`requestedExecutionLevel="asInvoker"`).

**Strict Driver Safety Rule:** TranssionLab **never** silently installs unsigned kernel drivers and **never** disables Windows Driver Signature Enforcement (`bcdedit /set testsigning on`).

| Connection Mode | Expected USB VID:PID Examples | Recommended Signed Windows Driver |
| :--- | :--- | :--- |
| **ADB / Recovery / Sideload** | `2ECC:*`, `18D1:4EE7`, `18D1:D001` | Microsoft built-in `WinUSB` / Official WHQL **Google USB Driver** (`android_winusb.inf`) |
| **Fastboot / Fastbootd** | `18D1:4EE0`, `18D1:D00D`, `0BB4:0C01`, `2ECC:201C` | Official WHQL **Google USB Driver** (*Android Bootloader Interface*) |
| **MediaTek BROM / Preloader / META** | `0E8D:0003`, `0E8D:2000`, `0E8D:2006`, `0E8D:2007` | WHQL-signed **MediaTek USB VCOM Driver** (`usbser.sys`) |
| **Unisoc Download / DIAG** | `1782:4D00`, `1782:4D10`, `1782:4D12` | WHQL-signed **Unisoc / Spreadtrum SCI USB2Serial Driver** |

### Common Windows ConfigManager Error Codes Reported in Page #2
- **Code 0 (`Healthy`)**: Driver properly bound and active.
- **Code 28 (`MissingDriverCode28`)**: No INF driver bound. Update driver via Windows Optional Updates or official Google/OEM WHQL INF.
- **Code 10 (`CannotStartCode10`)**: Common on MediaTek Preloader (`0E8D:2000`) after the 2–3 second boot window elapses and the phone transitions to normal charging.
- **Code 43 (`DescriptorFailureCode43`)**: USB descriptor read failed — inspect USB-C cable quality or try a direct motherboard USB 2.0/3.0 port.
- **Code 52 (`UnsignedBlockedCode52`)**: An unsigned third-party driver was installed. Remove it and replace with a Microsoft/WHQL-signed driver.

---

## 2. Third-Party Software Licensing & Redistribution Boundaries

### Included / Runtime Libraries (MIT License — .NET Foundation / Microsoft)
- **.NET 8 Runtime & WPF (`Microsoft.NETCore.App`, `Microsoft.WindowsDesktop.App`)** — MIT License.
- **`Microsoft.Extensions.DependencyInjection` (8.0.1)** — MIT License.
- **`System.IO.Ports` (8.0.0), `System.Management` (8.0.0), `System.IO.Hashing` (8.0.0)** — MIT License.

### External Redistributable Utilities (User-Configured or Separately Downloaded)
- **Android SDK Platform-Tools (`adb.exe`, `fastboot.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`)**
  - License: **Apache License 2.0** (Copyright Google LLC / The Android Open Source Project).
  - Configure in **Page #17 (Settings)** or place in `tools/` / system `PATH`.

### Proprietary Components Explicitly NOT Redistributed
To maintain strict legal compliance, the following proprietary binaries and assets are **never** included in this repository or its release artifacts:
1. **Proprietary Stock Firmware Packages** (TECNO HiOS, Infinix XOS, and itelOS ROMs).
2. **MediaTek Proprietary Libraries & Auth Files** (`MetaCore.dll`, `SPMETA_DLL.dll`, signed Download Agents `MTK_AllInOne_DA.bin`, `.auth` certificates, SLA keys).
3. **Unisoc / Spreadtrum Proprietary Binaries** (`ResearchDownload.exe`, `FactoryDownload.exe`, OEM-signed `fdl1-sign.bin` / `fdl2-sign.bin`).
4. **Private Cryptographic Signing Keys or OEM Credentials**.
