using System.Collections.ObjectModel;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.ViewModels.Mvvm;

namespace TranssionLab.ViewModels.Pages;

// Page 6: MediaTek Tools (Section 2B)
public sealed class MediaTekToolsViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IUsbInterfaceProvider _usb;
    private readonly IMediaTekProvider _mtk;

    public override int PageNumber => 6;
    public override string PageTitle => "MediaTek Tools";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "Ⓜ";

    public ObservableCollection<DetectedUsbDevice> DetectedMtkEndpoints { get; } = new();

    private string _selectedComPort = string.Empty;
    public string SelectedComPort
    {
        get => _selectedComPort;
        set => SetProperty(ref _selectedComPort, value);
    }

    private MediaTekHandshakeProbeResult? _lastProbeResult;
    public MediaTekHandshakeProbeResult? LastProbeResult
    {
        get => _lastProbeResult;
        private set => SetProperty(ref _lastProbeResult, value);
    }

    public AsyncRelayCommand ScanMtkInterfacesCommand { get; }
    public AsyncRelayCommand ProbeReadOnlyBromCommand { get; }

    public MediaTekToolsViewModel(IDeviceDiscoveryProvider discovery, IUsbInterfaceProvider usb, IMediaTekProvider mtk)
    {
        _discovery = discovery;
        _usb = usb;
        _mtk = mtk;
        ScanMtkInterfacesCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(RefreshMtkAsync, "MediaTek USB scan failed"));
        ProbeReadOnlyBromCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(ProbeWithWaitAsync, "MediaTek BROM probe failed"));
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) =>
        RunSafeActivityAsync(RefreshMtkAsync, "MediaTek USB scan failed");

    private async Task ProbeWithWaitAsync()
    {
        var portToProbe = SelectedComPort?.Trim();
        var activePorts = await _usb.EnumerateComPortsAsync().ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(portToProbe) ||
            !activePorts.Any(p => string.Equals(p.PortName, portToProbe, StringComparison.OrdinalIgnoreCase)))
        {
            var captured = await _discovery.WaitForVendorUsbEndpointAsync(
                "MediaTek BROM / Preloader Probe",
                u => u.DetectedChipsetVendor == ChipsetVendor.MediaTek &&
                     !string.IsNullOrWhiteSpace(u.AssociatedComPort) &&
                     u.ClassifiedMode is ConnectionMode.MediaTekBrom or ConnectionMode.MediaTekPreloader or ConnectionMode.MediaTekDownloadAgent,
                "Power off phone, hold Vol- / Vol+ if required, and connect USB cable",
                TimeSpan.FromSeconds(45),
                CreateWaitProgressCallback()).ConfigureAwait(true);

            portToProbe = captured.AssociatedComPort!;
            SelectedComPort = portToProbe;
            await RefreshMtkAsync().ConfigureAwait(true);
        }

        StatusBanner = $"Opening {portToProbe} for read-only MediaTek BROM/Preloader handshake...";
        LastProbeResult = await _mtk.ProbeReadOnlyBromOrPreloaderAsync(portToProbe).ConfigureAwait(true);
        StatusBanner = LastProbeResult.DiagnosticMessage;
    }

    private async Task RefreshMtkAsync()
    {
        var all = await _usb.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var filtered = _mtk.FilterMediaTekInterfaces(all);
        DetectedMtkEndpoints.Clear();
        foreach (var item in filtered)
        {
            DetectedMtkEndpoints.Add(item);
            if (string.IsNullOrWhiteSpace(SelectedComPort) && !string.IsNullOrWhiteSpace(item.AssociatedComPort))
            {
                SelectedComPort = item.AssociatedComPort!;
            }
        }
        StatusBanner = filtered.Count > 0
            ? $"Detected {filtered.Count} MediaTek USB endpoint(s). Detection does NOT imply unrestricted storage/DA access."
            : "No MediaTek BROM (0E8D:0003) or Preloader (0E8D:2000) attached yet — click 'Run Read-Only BROM Probe' to enter 'Waiting for device...' mode.";
    }
}

// Page 7: Dedicated META Mode Panel (Section 2D)
public sealed class MetaModeViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IUsbInterfaceProvider _usb;
    private readonly IMetaModeProvider _meta;

    public override int PageNumber => 7;
    public override string PageTitle => "META Mode";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "⚙";

    private string _providerInstallationStatus = string.Empty;
    public string ProviderInstallationStatus { get => _providerInstallationStatus; private set => SetProperty(ref _providerInstallationStatus, value); }

    public ObservableCollection<MetaInterfaceDetection> DetectedMetaEndpoints { get; } = new();

    private MetaInterfaceDetection? _selectedMetaEndpoint;
    public MetaInterfaceDetection? SelectedMetaEndpoint
    {
        get => _selectedMetaEndpoint;
        set => SetProperty(ref _selectedMetaEndpoint, value);
    }

    private MetaSessionDiagnosticResult? _lastDiagnostic;
    public MetaSessionDiagnosticResult? LastDiagnostic
    {
        get => _lastDiagnostic;
        private set => SetProperty(ref _lastDiagnostic, value);
    }

    public AsyncRelayCommand ScanMetaEndpointsCommand { get; }
    public AsyncRelayCommand VerifyMetaSessionCommand { get; }

    public MetaModeViewModel(IDeviceDiscoveryProvider discovery, IUsbInterfaceProvider usb, IMetaModeProvider meta)
    {
        _discovery = discovery;
        _usb = usb;
        _meta = meta;
        ScanMetaEndpointsCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(RefreshMetaAsync, "META scan failed"));
        VerifyMetaSessionCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(VerifyMetaWithWaitAsync, "META verification failed"));
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) =>
        RunSafeActivityAsync(RefreshMetaAsync, "META scan failed");

    private async Task VerifyMetaWithWaitAsync()
    {
        var target = SelectedMetaEndpoint?.UsbDevice;
        if (target is null)
        {
            target = await _discovery.WaitForVendorUsbEndpointAsync(
                "Verify MediaTek META Session",
                u => u.ClassifiedMode == ConnectionMode.MediaTekMeta,
                "Connect device in MediaTek META mode via USB",
                TimeSpan.FromSeconds(45),
                CreateWaitProgressCallback()).ConfigureAwait(true);
            await RefreshMetaAsync().ConfigureAwait(true);
        }

        StatusBanner = $"Diagnosing META endpoint {target.VidPidDisplay}...";
        LastDiagnostic = await _meta.DiagnoseMetaConnectionAsync(target).ConfigureAwait(true);
        StatusBanner = LastDiagnostic.DiagnosticSummary;
    }

    private async Task RefreshMetaAsync()
    {
        _meta.IsAuthorizedMetaLibraryInstalled(out var detail);
        ProviderInstallationStatus = detail;

        var usbList = await _usb.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var detections = _meta.DetectMetaInterfaces(usbList);
        DetectedMetaEndpoints.Clear();
        foreach (var d in detections) DetectedMetaEndpoints.Add(d);
        SelectedMetaEndpoint = DetectedMetaEndpoints.FirstOrDefault();

        StatusBanner = detections.Count > 0
            ? $"Found {detections.Count} recognized META USB endpoint(s)."
            : "No MediaTek META USB interface currently connected — click 'Verify Selected META Interface' to wait for a META device.";
    }
}

// Page 8: SPD / Unisoc Tools (Section 2C)
public sealed class SpdUnisocToolsViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IUsbInterfaceProvider _usb;
    private readonly ISpdUnisocProvider _unisoc;

    public override int PageNumber => 8;
    public override string PageTitle => "SPD/Unisoc Tools";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "Ⓤ";

    public ObservableCollection<DetectedUsbDevice> DetectedUnisocEndpoints { get; } = new();

    private string _selectedComPort = string.Empty;
    public string SelectedComPort
    {
        get => _selectedComPort;
        set => SetProperty(ref _selectedComPort, value);
    }

    private UnisocBootProbeResult? _lastProbeResult;
    public UnisocBootProbeResult? LastProbeResult
    {
        get => _lastProbeResult;
        private set => SetProperty(ref _lastProbeResult, value);
    }

    public AsyncRelayCommand ScanUnisocEndpointsCommand { get; }
    public AsyncRelayCommand ProbeDownloadBannerCommand { get; }

    public SpdUnisocToolsViewModel(IDeviceDiscoveryProvider discovery, IUsbInterfaceProvider usb, ISpdUnisocProvider unisoc)
    {
        _discovery = discovery;
        _usb = usb;
        _unisoc = unisoc;
        ScanUnisocEndpointsCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(RefreshUnisocAsync, "Unisoc USB scan failed"));
        ProbeDownloadBannerCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(ProbeUnisocWithWaitAsync, "Unisoc boot probe failed"));
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) =>
        RunSafeActivityAsync(RefreshUnisocAsync, "Unisoc USB scan failed");

    private async Task ProbeUnisocWithWaitAsync()
    {
        var portToProbe = SelectedComPort?.Trim();
        var activePorts = await _usb.EnumerateComPortsAsync().ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(portToProbe) ||
            !activePorts.Any(p => string.Equals(p.PortName, portToProbe, StringComparison.OrdinalIgnoreCase)))
        {
            var captured = await _discovery.WaitForVendorUsbEndpointAsync(
                "Unisoc Boot ROM Probe (BSL_REP_VER)",
                u => u.DetectedChipsetVendor == ChipsetVendor.Unisoc &&
                     !string.IsNullOrWhiteSpace(u.AssociatedComPort),
                "Power off phone, hold Boot / Vol-, and connect USB cable",
                TimeSpan.FromSeconds(45),
                CreateWaitProgressCallback()).ConfigureAwait(true);

            portToProbe = captured.AssociatedComPort!;
            SelectedComPort = portToProbe;
            await RefreshUnisocAsync().ConfigureAwait(true);
        }

        StatusBanner = $"Probing Unisoc HDLC 0x7E boot banner on {portToProbe}...";
        LastProbeResult = await _unisoc.ProbeReadOnlyDownloadVersionAsync(portToProbe).ConfigureAwait(true);
        StatusBanner = LastProbeResult.DiagnosticMessage;
    }

    private async Task RefreshUnisocAsync()
    {
        var all = await _usb.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var filtered = _unisoc.FilterUnisocInterfaces(all);
        DetectedUnisocEndpoints.Clear();
        foreach (var item in filtered)
        {
            DetectedUnisocEndpoints.Add(item);
            if (string.IsNullOrWhiteSpace(SelectedComPort) && !string.IsNullOrWhiteSpace(item.AssociatedComPort))
                SelectedComPort = item.AssociatedComPort!;
        }
        StatusBanner = filtered.Count > 0
            ? $"Detected {filtered.Count} Unisoc/Spreadtrum USB endpoint(s) (VID 0x1782)."
            : "No Unisoc Download (1782:4D00) endpoint attached yet — click 'Probe Unisoc Boot ROM Banner' to enter 'Waiting for device...' mode.";
    }
}

// Page 9: DIAG and Diagnostics (Section 2E)
public sealed class DiagDiagnosticsViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IUsbInterfaceProvider _usb;
    private readonly IDiagnosticProvider _diag;

    public override int PageNumber => 9;
    public override string PageTitle => "DIAG and Diagnostics";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "∿";

    public ObservableCollection<DiagInterfaceAssessment> ClassifiedDiagEndpoints { get; } = new();

    private DiagInterfaceAssessment? _selectedEndpoint;
    public DiagInterfaceAssessment? SelectedEndpoint
    {
        get => _selectedEndpoint;
        set
        {
            if (SetProperty(ref _selectedEndpoint, value))
                RunReadOnlyVersionQueryCommand.RaiseCanExecuteChanged();
        }
    }

    private DiagReadOnlyQueryResult? _lastQueryResult;
    public DiagReadOnlyQueryResult? LastQueryResult
    {
        get => _lastQueryResult;
        private set => SetProperty(ref _lastQueryResult, value);
    }

    public AsyncRelayCommand ScanDiagEndpointsCommand { get; }
    public AsyncRelayCommand RunReadOnlyVersionQueryCommand { get; }

    public DiagDiagnosticsViewModel(IDeviceDiscoveryProvider discovery, IUsbInterfaceProvider usb, IDiagnosticProvider diag)
    {
        _discovery = discovery;
        _usb = usb;
        _diag = diag;
        ScanDiagEndpointsCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(RefreshDiagAsync, "DIAG scan failed"));
        RunReadOnlyVersionQueryCommand = new AsyncRelayCommand(
            () => RunSafeActivityAsync(QueryDiagWithWaitAsync, "DIAG version query failed"),
            () => SelectedEndpoint is null || SelectedEndpoint.SupportsReadOnlyVersionProbe);
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) =>
        RunSafeActivityAsync(RefreshDiagAsync, "DIAG scan failed");

    private async Task QueryDiagWithWaitAsync()
    {
        if (SelectedEndpoint is null)
        {
            await _discovery.WaitForVendorUsbEndpointAsync(
                "Read-Only DIAG Version Probe",
                u => u.ClassifiedMode is ConnectionMode.QualcommDiag or ConnectionMode.UnisocDiag or ConnectionMode.VendorDiag or ConnectionMode.MediaTekMeta,
                "Connect device with an active diagnostic USB serial interface",
                TimeSpan.FromSeconds(45),
                CreateWaitProgressCallback()).ConfigureAwait(true);
            await RefreshDiagAsync().ConfigureAwait(true);
        }

        if (SelectedEndpoint is null)
            return;

        if (!SelectedEndpoint.SupportsReadOnlyVersionProbe)
        {
            StatusBanner = SelectedEndpoint.GuardrailNotice;
            return;
        }

        StatusBanner = $"Executing read-only version query on {SelectedEndpoint.ProtocolFamily}...";
        LastQueryResult = await _diag.QueryReadOnlyDiagnosticVersionAsync(SelectedEndpoint).ConfigureAwait(true);
        StatusBanner = LastQueryResult.Summary;
    }

    private async Task RefreshDiagAsync()
    {
        var usbList = await _usb.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var items = _diag.ClassifyDiagnosticInterfaces(usbList);
        ClassifiedDiagEndpoints.Clear();
        foreach (var i in items) ClassifiedDiagEndpoints.Add(i);
        SelectedEndpoint = ClassifiedDiagEndpoints.FirstOrDefault();

        StatusBanner = items.Count > 0
            ? $"Classified {items.Count} diagnostic endpoint(s). Protected NV/radio writes are strictly disabled."
            : "No DIAG USB interface detected. Click 'Execute Documented Read-Only DIAG Version Probe' to wait for a compatible DIAG device.";
    }
}

// Page 10: Bootloader Manager — Android 16 & Earlier (Section 5)
public sealed class BootloaderManagerViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IBootloaderManager _bootloaderManager;

    public override int PageNumber => 10;
    public override string PageTitle => "Bootloader Manager";
    public override string NavigationGroup => "SERVICING & FIRMWARE";
    public override string IconGlyph => "🔓";

    private BootloaderAssessment? _currentAssessment;
    public BootloaderAssessment? CurrentAssessment
    {
        get => _currentAssessment;
        private set
        {
            if (SetProperty(ref _currentAssessment, value))
            {
                ExecuteOfficialUnlockCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _confirmationInput = string.Empty;
    public string ConfirmationInput
    {
        get => _confirmationInput;
        set
        {
            if (SetProperty(ref _confirmationInput, value))
                ExecuteOfficialUnlockCommand.RaiseCanExecuteChanged();
        }
    }

    private BootloaderOperationResult? _lastResult;
    public BootloaderOperationResult? LastResult
    {
        get => _lastResult;
        private set => SetProperty(ref _lastResult, value);
    }

    public AsyncRelayCommand AssessBootloaderCommand { get; }
    public AsyncRelayCommand ExecuteOfficialUnlockCommand { get; }

    public BootloaderManagerViewModel(IDeviceDiscoveryProvider discovery, IBootloaderManager bootloaderManager)
    {
        _discovery = discovery;
        _bootloaderManager = bootloaderManager;

        AssessBootloaderCommand = new AsyncRelayCommand(() => RunSafeActivityAsync(AssessWithWaitAsync, "Bootloader assessment failed"));
        ExecuteOfficialUnlockCommand = new AsyncRelayCommand(
            () => RunSafeActivityAsync(ExecuteUnlockWithWaitAsync, "Official unlock failed"),
            () => CurrentAssessment is { IsUnlockWorkflowPermitted: true } &&
                  string.Equals(ConfirmationInput?.Trim(), CurrentAssessment.ExpectedConfirmationPhrase, StringComparison.Ordinal));

        _discovery.InventoryChanged += (_, _) => UiInteractionBridge.RunOnUiThread(() => _ = AssessIfSelectedQuietlyAsync());
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default) => AssessIfSelectedQuietlyAsync();

    private async Task AssessIfSelectedQuietlyAsync()
    {
        try
        {
            if (_discovery.SelectedDevice is { } dev)
            {
                CurrentAssessment = await _bootloaderManager.AssessDeviceAsync(dev).ConfigureAwait(true);
                StatusBanner = CurrentAssessment.StatusSummary;
            }
            else
            {
                CurrentAssessment = null;
                StatusBanner = _discovery.RequiresExplicitSelection
                    ? "Select one device explicitly before assessing bootloader state."
                    : "Click 'Re-Assess Bootloader' to wait for a device automatically, or connect in ADB / Fastboot mode.";
            }
        }
        catch (Exception ex)
        {
            StatusBanner = ex.Message;
        }
    }

    private async Task AssessWithWaitAsync()
    {
        var dev = await _discovery.WaitForDeviceAsync(
            "Assess Bootloader State",
            [ConnectionMode.AdbAuthorized, ConnectionMode.FastbootBootloader, ConnectionMode.FastbootdUserspace],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

        StatusBanner = $"Assessing bootloader policy and lock state on {dev.DeviceKey}...";
        CurrentAssessment = await _bootloaderManager.AssessDeviceAsync(dev).ConfigureAwait(true);
        StatusBanner = CurrentAssessment.StatusSummary;
    }

    private async Task ExecuteUnlockWithWaitAsync()
    {
        if (CurrentAssessment is null)
            return;

        var dev = await _discovery.WaitForDeviceAsync(
            "Official Bootloader Unlock",
            [ConnectionMode.FastbootBootloader],
            onProgress: CreateWaitProgressCallback()).ConfigureAwait(true);

        StatusBanner = $"Executing official unlock on {dev.TransportSerial} — approve prompt on handset screen with Volume Up...";
        LastResult = await _bootloaderManager.ExecuteVerifiedUnlockAsync(dev, CurrentAssessment, ConfirmationInput).ConfigureAwait(true);
        StatusBanner = LastResult.Summary;
        ConfirmationInput = string.Empty;
        CurrentAssessment = await _bootloaderManager.AssessDeviceAsync(dev).ConfigureAwait(true);
    }
}
