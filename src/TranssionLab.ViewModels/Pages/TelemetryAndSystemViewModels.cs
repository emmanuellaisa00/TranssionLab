using System.Collections.ObjectModel;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.ViewModels.Mvvm;

namespace TranssionLab.ViewModels.Pages;

// Page 15: Persistent Operation History
public sealed class OperationHistoryViewModel : PageViewModelBase
{
    private readonly IOperationHistoryStore _historyStore;

    public override int PageNumber => 15;
    public override string PageTitle => "Operation History";
    public override string NavigationGroup => "PROFILES & TELEMETRY";
    public override string IconGlyph => "🕒";

    public ObservableCollection<OperationHistoryRecord> Records { get; } = new();

    private OperationHistoryRecord? _selectedRecord;
    public OperationHistoryRecord? SelectedRecord
    {
        get => _selectedRecord;
        set => SetProperty(ref _selectedRecord, value);
    }

    public RelayCommand RefreshHistoryCommand { get; }

    public OperationHistoryViewModel(IOperationHistoryStore historyStore)
    {
        _historyStore = historyStore;
        RefreshHistoryCommand = new RelayCommand(Reload);
        Reload();
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        Reload();
        return Task.CompletedTask;
    }

    public void Reload()
    {
        Records.Clear();
        foreach (var r in _historyStore.GetRecords())
            Records.Add(r);
        SelectedRecord = Records.FirstOrDefault();
        StatusBanner = $"{Records.Count} archived operation record(s).";
    }
}

// Page 16: Live Logs & Troubleshooting Export (Section 8)
public sealed class LiveLogsViewModel : PageViewModelBase
{
    private readonly IStructuredLogger _logger;
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IProtocolCapabilityService _capabilities;
    private readonly IUsbInterfaceProvider _usb;

    public override int PageNumber => 16;
    public override string PageTitle => "Live Logs";
    public override string NavigationGroup => "PROFILES & TELEMETRY";
    public override string IconGlyph => "▤";

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyFilter();
        }
    }

    private LogSeverity _selectedMinSeverity = LogSeverity.Trace;
    public LogSeverity SelectedMinSeverity
    {
        get => _selectedMinSeverity;
        set
        {
            if (SetProperty(ref _selectedMinSeverity, value))
                ApplyFilter();
        }
    }

    private string _selectedProviderFilter = "All";
    public string SelectedProviderFilter
    {
        get => _selectedProviderFilter;
        set
        {
            if (SetProperty(ref _selectedProviderFilter, value))
                ApplyFilter();
        }
    }

    private string _exportPathTxt;
    public string ExportPathTxt { get => _exportPathTxt; set => SetProperty(ref _exportPathTxt, value); }

    private string _exportPathJson;
    public string ExportPathJson { get => _exportPathJson; set => SetProperty(ref _exportPathJson, value); }

    private string _troubleshootingBundleText = string.Empty;
    public string TroubleshootingBundleText { get => _troubleshootingBundleText; private set => SetProperty(ref _troubleshootingBundleText, value); }

    public IReadOnlyList<LogSeverity> SeverityOptions { get; } = Enum.GetValues<LogSeverity>();

    public ObservableCollection<StructuredLogEntry> VisibleEntries { get; } = new();

    public RelayCommand RefreshLogsCommand { get; }
    public RelayCommand ClearDisplayOnlyCommand { get; }
    public AsyncRelayCommand ExportTxtCommand { get; }
    public AsyncRelayCommand ExportJsonCommand { get; }
    public RelayCommand GenerateTroubleshootingBundleCommand { get; }
    public RelayCommand CopyTroubleshootingBundleCommand { get; }

    public LiveLogsViewModel(
        IStructuredLogger logger,
        IDeviceDiscoveryProvider discovery,
        IProtocolCapabilityService capabilities,
        IUsbInterfaceProvider usb,
        IAppSettingsService settings)
    {
        _logger = logger;
        _discovery = discovery;
        _capabilities = capabilities;
        _usb = usb;

        var exportDir = Path.Combine(settings.Current.WorkspaceDataDirectory, "exports");
        _exportPathTxt = Path.Combine(exportDir, $"session-{_logger.CurrentSessionId}.txt");
        _exportPathJson = Path.Combine(exportDir, $"session-{_logger.CurrentSessionId}.json");

        RefreshLogsCommand = new RelayCommand(ApplyFilter);
        ClearDisplayOnlyCommand = new RelayCommand(() =>
        {
            _logger.ClearDisplayOnly();
            ApplyFilter();
            StatusBanner = $"Cleared live display view. Archived session logs ({_logger.GetArchivedSessionEntries().Count} entries) remain intact.";
        });

        ExportTxtCommand = new AsyncRelayCommand(async () =>
        {
            await _logger.ExportTxtAsync(ExportPathTxt).ConfigureAwait(true);
            StatusBanner = $"Exported TXT log archive to '{ExportPathTxt}'.";
        });

        ExportJsonCommand = new AsyncRelayCommand(async () =>
        {
            await _logger.ExportJsonAsync(ExportPathJson).ConfigureAwait(true);
            StatusBanner = $"Exported JSON log archive to '{ExportPathJson}'.";
        });

        GenerateTroubleshootingBundleCommand = new RelayCommand(() =>
        {
            TroubleshootingBundleText = _logger.BuildTroubleshootingBundle(
                _discovery.ConnectedDevices,
                _capabilities.GetAllProviderSummaries(),
                _usb.GetConnectionHistory());
            StatusBanner = "Generated redacted diagnostic troubleshooting bundle ready to copy.";
        });

        CopyTroubleshootingBundleCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(TroubleshootingBundleText))
            {
                GenerateTroubleshootingBundleCommand.Execute(null);
            }
            UiInteractionBridge.CopyTextToClipboard?.Invoke(TroubleshootingBundleText);
            StatusBanner = "Copied redacted troubleshooting bundle to Windows Clipboard.";
        });

        ApplyFilter();
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        ApplyFilter();
        return Task.CompletedTask;
    }

    public void ApplyFilter()
    {
        var items = _logger.GetDisplayEntries(SearchText, SelectedMinSeverity, SelectedProviderFilter);
        VisibleEntries.Clear();
        foreach (var e in items)
            VisibleEntries.Add(e);
        StatusBanner = $"Displaying {VisibleEntries.Count} live entry(ies) (Total archived in session: {_logger.GetArchivedSessionEntries().Count}).";
    }
}

// Page 17: Offline-First Settings
public sealed class SettingsViewModel : PageViewModelBase
{
    private readonly IAppSettingsService _settingsService;

    public override int PageNumber => 17;
    public override string PageTitle => "Settings";
    public override string NavigationGroup => "SYSTEM";
    public override string IconGlyph => "⚙";

    private string _adbPath;
    public string AdbPath { get => _adbPath; set => SetProperty(ref _adbPath, value); }

    private string _fastbootPath;
    public string FastbootPath { get => _fastbootPath; set => SetProperty(ref _fastbootPath, value); }

    private string _optionalMetaDllPath;
    public string OptionalMetaDllPath { get => _optionalMetaDllPath; set => SetProperty(ref _optionalMetaDllPath, value); }

    private string _workspaceDirectory;
    public string WorkspaceDirectory { get => _workspaceDirectory; set => SetProperty(ref _workspaceDirectory, value); }

    private bool _autoPollUsb;
    public bool AutoPollUsb { get => _autoPollUsb; set => SetProperty(ref _autoPollUsb, value); }

    private bool _redactLogs;
    public bool RedactLogs { get => _redactLogs; set => SetProperty(ref _redactLogs, value); }

    private bool _strictProfileBeforeFlash;
    public bool StrictProfileBeforeFlash { get => _strictProfileBeforeFlash; set => SetProperty(ref _strictProfileBeforeFlash, value); }

    public AsyncRelayCommand SaveSettingsCommand { get; }

    public SettingsViewModel(IAppSettingsService settingsService)
    {
        _settingsService = settingsService;
        var cur = settingsService.Current;
        _adbPath = cur.AdbExecutablePath;
        _fastbootPath = cur.FastbootExecutablePath;
        _optionalMetaDllPath = cur.OptionalMetaLibraryPath ?? string.Empty;
        _workspaceDirectory = cur.WorkspaceDataDirectory;
        _autoPollUsb = cur.AutoPollUsbChanges;
        _redactLogs = cur.RedactSensitiveDataInLogs;
        _strictProfileBeforeFlash = cur.RequireStrictProfileMatchBeforeFlash;

        SaveSettingsCommand = new AsyncRelayCommand(async () =>
        {
            var updated = new AppSettingsModel
            {
                AdbExecutablePath = AdbPath.Trim(),
                FastbootExecutablePath = FastbootPath.Trim(),
                OptionalMetaLibraryPath = string.IsNullOrWhiteSpace(OptionalMetaDllPath) ? null : OptionalMetaDllPath.Trim(),
                WorkspaceDataDirectory = WorkspaceDirectory.Trim(),
                AutoPollUsbChanges = AutoPollUsb,
                RedactSensitiveDataInLogs = RedactLogs,
                RequireStrictProfileMatchBeforeFlash = StrictProfileBeforeFlash
            };
            await _settingsService.SaveAsync(updated).ConfigureAwait(true);
            StatusBanner = "Settings saved locally. Offline-first mode active (no cloud accounts or telemetry).";
        });
    }
}

public sealed record DocumentationTopic(string Title, string Category, string MarkdownContent);

// Page 18: Offline Documentation & About
public sealed class DocumentationAboutViewModel : PageViewModelBase
{
    public override int PageNumber => 18;
    public override string PageTitle => "Documentation & About";
    public override string NavigationGroup => "SYSTEM";
    public override string IconGlyph => "ℹ";

    public ObservableCollection<DocumentationTopic> Topics { get; } = new();

    private DocumentationTopic? _selectedTopic;
    public DocumentationTopic? SelectedTopic
    {
        get => _selectedTopic;
        set => SetProperty(ref _selectedTopic, value);
    }

    public DocumentationAboutViewModel()
    {
        Topics.Add(new DocumentationTopic(
            "1. Architecture & Honest Capability Policy",
            "Overview",
            """
            TRANSSIONLAB UNIVERSAL ANDROID SERVICING SUITE (v1.0.0)
            Target OS: Windows 10 & Windows 11 (x64, ARM64-ready) | Offline-First

            Core Engineering Principles:
            • Every protocol is isolated behind its own service contract (USB/PnP, ADB, Recovery/Sideload, Fastboot/Fastbootd, MediaTek BROM/Preloader, META Mode, Unisoc/SPD, and DIAG).
            • Data Provenance is tracked on every field: DETECTED, INFERRED, or VERIFIED.
            • Exact device marketing models (TECNO / Infinix / itel) are NEVER inferred from chipset codes (e.g., MT6789 or T606) alone.
            • When two or more phones are attached simultaneously, TranssionLab blocks ambiguous execution and requires explicit device selection.
            • Security & Compliance: TranssionLab implements ZERO security bypasses, BROM/SLA exploits, unauthorized authentication circumvention, FRP/anti-theft bypasses, or protected IMEI/NVRAM modifications.
            """));

        Topics.Add(new DocumentationTopic(
            "2. Protocol Support Matrix",
            "Protocols",
            """
            PROTOCOL IMPLEMENTATION & VERIFICATION SUMMARY:
            • USB & Windows PnP Discovery: Implemented & Unit-Tested (VID/PID/MI classification for 0x2ECC Transsion, 0x18D1 Google, 0x0E8D MediaTek, 0x1782 Unisoc, 0x05C6 Qualcomm; Driver error codes 10/22/28/43/52).
            • ADB & Recovery Sideload: Implemented & Unit-Tested (device/unauthorized/offline/recovery/sideload states, getprop identity, battery/storage health, reboot modes, verified ZIP sideload).
            • Fastboot & Userspace Fastbootd: Implemented & Unit-Tested (is-userspace differentiation, getvar inspection, active slot switching, official bootloader unlock with post-verification, 11-step validated partition flashing).
            • MediaTek BROM / Preloader: Read-Only Diagnostics Implemented & Unit-Tested (4-byte sync A0 0A 50 05 -> 5F F5 AF FA, GET_HW_CODE 0xFD, GET_TARGET_CONFIG 0xD8 SBC/SLA/DAA detection, Text & XML Scatter inspection). SLA/DAA flashing requires OEM-signed DA.
            • MediaTek META Mode: Honest USB Detection & Provider Readiness Check Implemented (requires external licensed OEM ModemMETA library for active modem/AP sessions).
            • Spreadtrum / Unisoc: Read-Only Diagnostics & PAC Inspection Implemented & Unit-Tested (HDLC 0x7E framing, CRC-16 XMODEM, BSL_REP_VER 0x81 boot banner probe, binary .pac parser & extractor). Secure Boot flashing requires OEM-signed FDL1/FDL2.
            • DIAG Interfaces: Chipset-Aware Classification & Read-Only Qualcomm DIAG_VERNO_F (0x00) Implemented; cross-chipset QCDM and all NV writes are blocked.
            """));

        Topics.Add(new DocumentationTopic(
            "3. Adding Hardware-Verified Device Profiles",
            "Profiles",
            """
            HOW TO ADD & HARDWARE-VERIFY A TRANSSION DEVICE PROFILE:
            1. Connect the physical TECNO, Infinix, or itel device over authorized ADB and export its Device Snapshot JSON in Device Explorer.
            2. Boot into Fastboot and record 'fastboot getvar all' (verifying product, is-userspace, partition-size, and unlock policy).
            3. Create a JSON file adhering to the DeviceProfile schema (see profiles/*.json in the installation directory):
               - Set Exact ModelCodes (e.g., ["CL9", "TECNO CL9"]), Codename ("TECNO-CL9"), ChipsetCode ("MT6896"), and ProtectedPartitions.
               - Only set "VerificationLevel": "HardwareVerified" and "IsHardwareVerified": true AFTER executing and archiving physical bench test logs on that exact build fingerprint.
            4. Import the JSON file offline in Page 14 (Device Profiles -> Import JSON Profile).
            """));

        Topics.Add(new DocumentationTopic(
            "4. Driver Requirements & Third-Party Licensing",
            "Legal & Drivers",
            """
            WINDOWS DRIVER GUIDANCE (SAFE, SIGNED ONLY):
            • ADB / Fastboot / Fastbootd: Official WHQL-signed Google USB Driver (WinUSB.sys).
            • MediaTek Preloader / BROM / META: Official WHQL-signed MediaTek USB VCOM Driver (usbser.sys).
            • Unisoc Download / DIAG: Official WHQL-signed Unisoc SCI USB2Serial Driver.
            TranssionLab NEVER installs unsigned drivers or disables Windows Driver Signature Enforcement.

            THIRD-PARTY TOOLS & LICENSING:
            • TranssionLab Core & UI: Original C# / .NET 8 WPF implementation.
            • Android Platform-Tools (adb.exe / fastboot.exe): Apache License 2.0 (Google / AOSP). Configure path in Settings or place in tools/platform-tools/.
            • Proprietary MediaTek ModemMETA DLLs, OEM-signed Download Agents (DA), and Unisoc signed FDL binaries are NOT redistributed.
            """));

        SelectedTopic = Topics[0];
        StatusBanner = "100% Offline Documentation loaded.";
    }
}
