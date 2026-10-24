using System.Diagnostics;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;

namespace TranssionLab.Protocols.Discovery;

/// <summary>
/// Unified device connection discovery, live "Waiting for device..." engine & correlation service (Sections 2 & 3).
/// Enforces:
/// 1) Explicit selection whenever multiple devices are connected (never silently chooses one).
/// 2) Strict provenance (Detected vs Inferred vs Verified) — never infers exact phone model from chipset alone.
/// 3) Real-time cancellable "Waiting for device..." polling across ADB, Fastboot, Fastbootd, Recovery, Sideload, BROM, Unisoc, META & DIAG.
/// </summary>
public sealed class DeviceDiscoveryProvider : IDeviceDiscoveryProvider, IDisposable
{
    private readonly IUsbInterfaceProvider _usbProvider;
    private readonly IAdbProvider _adbProvider;
    private readonly IFastbootProvider _fastbootProvider;
    private readonly IDeviceProfileRepository _profileRepo;
    private readonly IProtocolCapabilityService _capabilityService;
    private readonly IStructuredLogger _logger;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
    private List<ConnectedDeviceSnapshot> _connectedDevices = new();
    private string? _explicitSelectedKey;
    private CancellationTokenSource? _activeWaitCts;
    private string? _waitingStatusMessage;

    public TimeSpan DefaultWaitTimeout { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(850);
    public TimeSpan VendorPollInterval { get; set; } = TimeSpan.FromMilliseconds(400);

    public event EventHandler? InventoryChanged;
    public event EventHandler<string?>? WaitingStatusChanged;

    public DeviceDiscoveryProvider(
        IUsbInterfaceProvider usbProvider,
        IAdbProvider adbProvider,
        IFastbootProvider fastbootProvider,
        IDeviceProfileRepository profileRepo,
        IProtocolCapabilityService capabilityService,
        IStructuredLogger logger)
    {
        _usbProvider = usbProvider;
        _adbProvider = adbProvider;
        _fastbootProvider = fastbootProvider;
        _profileRepo = profileRepo;
        _capabilityService = capabilityService;
        _logger = logger;

        _usbProvider.UsbDeviceEvent += OnUsbDeviceTopologyEvent;
    }

    private async void OnUsbDeviceTopologyEvent(object? sender, UsbConnectionHistoryEvent e)
    {
        try
        {
            await RefreshInventoryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("DeviceDiscovery", "BackgroundUsbSync", ex.Message);
        }
    }

    public bool IsWaitingForDevice
    {
        get { lock (_gate) return !string.IsNullOrEmpty(_waitingStatusMessage); }
    }

    public string? WaitingStatusMessage
    {
        get { lock (_gate) return _waitingStatusMessage; }
    }

    private void UpdateWaitingStatus(string? message)
    {
        lock (_gate)
        {
            _waitingStatusMessage = message;
        }

        try
        {
            WaitingStatusChanged?.Invoke(this, message);
        }
        catch
        {
            // Never allow UI subscriber error to break transport polling
        }
    }

    public void CancelActiveWait()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _activeWaitCts;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already completed
        }
    }

    public IReadOnlyList<ConnectedDeviceSnapshot> ConnectedDevices
    {
        get { lock (_gate) return _connectedDevices.ToList(); }
    }

    public bool RequiresExplicitSelection
    {
        get
        {
            lock (_gate)
            {
                return _connectedDevices.Count > 1 &&
                       (string.IsNullOrWhiteSpace(_explicitSelectedKey) ||
                        !_connectedDevices.Any(d => string.Equals(d.DeviceKey, _explicitSelectedKey, StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    public ConnectedDeviceSnapshot? SelectedDevice
    {
        get
        {
            lock (_gate)
            {
                if (_connectedDevices.Count == 0)
                    return null;

                if (_connectedDevices.Count == 1)
                {
                    if (string.IsNullOrWhiteSpace(_explicitSelectedKey))
                        return _connectedDevices[0];

                    return _connectedDevices.FirstOrDefault(d => string.Equals(d.DeviceKey, _explicitSelectedKey, StringComparison.OrdinalIgnoreCase))
                        ?? _connectedDevices[0];
                }

                // > 1 device connected: NEVER silently auto-select!
                if (string.IsNullOrWhiteSpace(_explicitSelectedKey))
                    return null;

                return _connectedDevices.FirstOrDefault(d => string.Equals(d.DeviceKey, _explicitSelectedKey, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public void SelectDeviceExplicitly(string? deviceKey)
    {
        lock (_gate)
        {
            _explicitSelectedKey = deviceKey;
        }
        _logger.LogInfo("DeviceDiscovery", $"User explicitly selected device: {deviceKey ?? "(Cleared)"}", null, deviceKey);
        InventoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public ConnectedDeviceSnapshot RequireExplicitlySelectedDevice()
    {
        lock (_gate)
        {
            if (_connectedDevices.Count == 0)
            {
                throw new ServicingException("NO_DEVICE_CONNECTED", "No connected Android/Transsion device is currently detected.");
            }

            if (_connectedDevices.Count > 1 && string.IsNullOrWhiteSpace(_explicitSelectedKey))
            {
                throw new AmbiguousDeviceSelectionException(_connectedDevices.Count);
            }

            var selected = SelectedDevice;
            if (selected is null)
            {
                throw new AmbiguousDeviceSelectionException(_connectedDevices.Count);
            }

            return selected;
        }
    }

    public async Task<ConnectedDeviceSnapshot> WaitForDeviceAsync(
        string activityName,
        IReadOnlyCollection<ConnectionMode>? acceptedModes = null,
        TimeSpan? timeout = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityName);

        var maxWait = timeout ?? DefaultWaitTimeout;
        var modeDesc = FormatAcceptedModes(acceptedModes);

        // Quick check against current cached state first when already connected in target mode
        if (TryMatchCurrentOrThrowAmbiguous(acceptedModes, out var immediateMatch))
        {
            return immediateMatch!;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _activeWaitCts = linkedCts;
        }

        var sw = Stopwatch.StartNew();
        var initialMsg = BuildWaitingMessage(activityName, modeDesc, SelectedDevice, 0, maxWait);
        UpdateWaitingStatus(initialMsg);
        onProgress?.Invoke(initialMsg);
        _logger.LogInfo("DeviceDiscovery", $"Waiting for device ({activityName})", $"Expected mode(s): {modeDesc}, Timeout: {maxWait.TotalSeconds:F0}s");

        try
        {
            while (true)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                await RefreshInventoryAsync(linkedCts.Token).ConfigureAwait(false);

                if (TryMatchCurrentOrThrowAmbiguous(acceptedModes, out var matched))
                {
                    _logger.LogInfo(
                        "DeviceDiscovery",
                        $"Device ready for '{activityName}': {matched!.DeviceKey} ({matched.ActiveConnectionMode})",
                        $"Elapsed wait: {sw.Elapsed.TotalSeconds:F1}s",
                        matched.DeviceKey);
                    return matched;
                }

                if (sw.Elapsed >= maxWait)
                {
                    var current = SelectedDevice;
                    var currentHint = current is not null
                        ? $" Device '{current.DeviceKey}' is currently in '{current.ActiveConnectionMode}' ({current.AuthorizationStatus})."
                        : " No matching USB/ADB/Fastboot endpoint appeared.";

                    throw new ServicingException(
                        "DEVICE_WAIT_TIMEOUT",
                        $"Timed out after {maxWait.TotalSeconds:F0}s waiting for device in {modeDesc} for '{activityName}'.{currentHint}");
                }

                var msg = BuildWaitingMessage(activityName, modeDesc, SelectedDevice, (int)sw.Elapsed.TotalSeconds, maxWait);
                UpdateWaitingStatus(msg);
                onProgress?.Invoke(msg);

                await Task.Delay(PollInterval, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("DeviceDiscovery", $"Cancelled waiting for device ({activityName})", "Wait cancelled by user or caller.");
            throw new OperationCanceledException($"Stopped waiting for device ({activityName}).");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeWaitCts, linkedCts))
                {
                    _activeWaitCts = null;
                }
            }
            UpdateWaitingStatus(null);
        }
    }

    public async Task<DetectedUsbDevice> WaitForVendorUsbEndpointAsync(
        string activityName,
        Func<DetectedUsbDevice, bool> predicate,
        string connectionHint,
        TimeSpan? timeout = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityName);
        ArgumentNullException.ThrowIfNull(predicate);

        var maxWait = timeout ?? DefaultWaitTimeout;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _activeWaitCts = linkedCts;
        }

        var sw = Stopwatch.StartNew();
        bool loggedStart = false;

        try
        {
            while (true)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                var usbDevices = await _usbProvider.EnumerateUsbDevicesAsync(linkedCts.Token).ConfigureAwait(false);
                var matches = usbDevices.Where(predicate).ToList();

                if (matches.Count > 1)
                {
                    throw new AmbiguousDeviceSelectionException(matches.Count);
                }

                if (matches.Count == 1)
                {
                    var found = matches[0];
                    if (loggedStart)
                    {
                        _logger.LogInfo(
                            "DeviceDiscovery",
                            $"Captured USB endpoint for '{activityName}': {found.VidPidDisplay} ({found.AssociatedComPort ?? found.InstanceId})",
                            $"Elapsed wait: {sw.Elapsed.TotalSeconds:F1}s",
                            found.InstanceId);
                    }
                    return found;
                }

                if (!loggedStart)
                {
                    loggedStart = true;
                    _logger.LogInfo("DeviceDiscovery", $"Waiting for device ({activityName})", connectionHint);
                }

                if (sw.Elapsed >= maxWait)
                {
                    throw new ServicingException(
                        "DEVICE_WAIT_TIMEOUT",
                        $"Timed out after {maxWait.TotalSeconds:F0}s waiting for device ({activityName}). {connectionHint}");
                }

                var msg = $"Waiting for device... [{activityName}] — {connectionHint} ({(int)sw.Elapsed.TotalSeconds}s / {(int)maxWait.TotalSeconds}s)";
                UpdateWaitingStatus(msg);
                onProgress?.Invoke(msg);

                await Task.Delay(VendorPollInterval, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("DeviceDiscovery", $"Cancelled waiting for device ({activityName})", "Wait cancelled by user or caller.");
            throw new OperationCanceledException($"Stopped waiting for device ({activityName}).");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeWaitCts, linkedCts))
                {
                    _activeWaitCts = null;
                }
            }
            UpdateWaitingStatus(null);
        }
    }

    private bool TryMatchCurrentOrThrowAmbiguous(
        IReadOnlyCollection<ConnectionMode>? acceptedModes,
        out ConnectedDeviceSnapshot? matched)
    {
        lock (_gate)
        {
            if (_connectedDevices.Count > 1 &&
                (string.IsNullOrWhiteSpace(_explicitSelectedKey) ||
                 !_connectedDevices.Any(d => string.Equals(d.DeviceKey, _explicitSelectedKey, StringComparison.OrdinalIgnoreCase))))
            {
                throw new AmbiguousDeviceSelectionException(_connectedDevices.Count);
            }

            var candidate = SelectedDevice;
            if (candidate is null)
            {
                matched = null;
                return false;
            }

            if (acceptedModes is null || acceptedModes.Count == 0 || acceptedModes.Contains(candidate.ActiveConnectionMode))
            {
                matched = candidate;
                return true;
            }

            matched = null;
            return false;
        }
    }

    private static string FormatAcceptedModes(IReadOnlyCollection<ConnectionMode>? modes)
    {
        if (modes is null || modes.Count == 0)
            return "any supported USB/ADB/Fastboot mode";

        return string.Join(" / ", modes.Select(m => m switch
        {
            ConnectionMode.AdbAuthorized => "Authorized ADB",
            ConnectionMode.AdbSideload => "Recovery ADB Sideload",
            ConnectionMode.Recovery => "Recovery",
            ConnectionMode.FastbootBootloader => "Bootloader Fastboot",
            ConnectionMode.FastbootdUserspace => "Userspace Fastbootd",
            ConnectionMode.MediaTekBrom => "MediaTek BROM",
            ConnectionMode.MediaTekPreloader => "MediaTek Preloader",
            ConnectionMode.UnisocDownload => "Unisoc Download",
            _ => m.ToString()
        }));
    }

    private static string BuildWaitingMessage(
        string activityName,
        string modeDesc,
        ConnectedDeviceSnapshot? currentDevice,
        int elapsedSeconds,
        TimeSpan maxWait)
    {
        var timerSuffix = $"({elapsedSeconds}s / {(int)maxWait.TotalSeconds}s)";
        if (currentDevice is null)
        {
            return $"Waiting for device... [{activityName}] — Connect phone via USB in {modeDesc} {timerSuffix}";
        }

        if (currentDevice.ActiveConnectionMode == ConnectionMode.AdbUnauthorized)
        {
            return $"Waiting for device... [{activityName}] — Phone detected ({currentDevice.TransportSerial}); unlock screen and tap 'Allow USB debugging' {timerSuffix}";
        }

        return $"Waiting for device... [{activityName}] — Phone is currently in {currentDevice.ActiveConnectionMode}; awaiting {modeDesc} {timerSuffix}";
    }

    public async Task<IReadOnlyList<ConnectedDeviceSnapshot>> RefreshInventoryAsync(CancellationToken ct = default)
    {
        await _refreshSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Trigger tool checks and enumerations concurrently
            var usbTask = _usbProvider.EnumerateUsbDevicesAsync(ct);
            var adbTask = _adbProvider.ListDevicesAsync(ct);
            var fbTask = _fastbootProvider.ListDevicesAsync(ct);

            await Task.WhenAll(usbTask, adbTask, fbTask).ConfigureAwait(false);

            var usbList = usbTask.Result;
            var adbList = adbTask.Result;
            var fbList = fbTask.Result;

            var snapshots = new List<ConnectedDeviceSnapshot>();
            var matchedUsbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Correlate ADB / Recovery / Sideload endpoints
            foreach (var adb in adbList)
            {
                var matchingUsb = usbList.FirstOrDefault(u =>
                    !string.IsNullOrWhiteSpace(u.SerialNumber) &&
                    string.Equals(u.SerialNumber, adb.Serial, StringComparison.OrdinalIgnoreCase));

                if (matchingUsb is not null)
                {
                    matchedUsbIds.Add(matchingUsb.InstanceId);
                }

                var snap = await BuildAdbSnapshotAsync(adb, matchingUsb, ct).ConfigureAwait(false);
                snapshots.Add(EnrichSupportedOps(snap));
            }

            // 2. Correlate Fastboot / Fastbootd endpoints
            foreach (var fb in fbList)
            {
                var matchingUsb = usbList.FirstOrDefault(u =>
                    !string.IsNullOrWhiteSpace(u.SerialNumber) &&
                    string.Equals(u.SerialNumber, fb.Serial, StringComparison.OrdinalIgnoreCase));

                if (matchingUsb is not null)
                {
                    matchedUsbIds.Add(matchingUsb.InstanceId);
                }

                var snap = await BuildFastbootSnapshotAsync(fb, matchingUsb, ct).ConfigureAwait(false);
                snapshots.Add(EnrichSupportedOps(snap));
            }

            // 3. Remaining USB-only interfaces (MediaTek BROM/Preloader/META, Unisoc Download/DIAG, Qualcomm DIAG, missing-driver fastboot/ADB)
            foreach (var usb in usbList.Where(u => !matchedUsbIds.Contains(u.InstanceId)))
            {
                var snap = BuildUsbOnlySnapshot(usb);
                snapshots.Add(EnrichSupportedOps(snap));
            }

            lock (_gate)
            {
                _connectedDevices = snapshots;
                if (_explicitSelectedKey is not null &&
                    !_connectedDevices.Any(d => string.Equals(d.DeviceKey, _explicitSelectedKey, StringComparison.OrdinalIgnoreCase)))
                {
                    // Previously selected device disconnected; clear selection so a newly attached second device is never silently chosen
                    _explicitSelectedKey = null;
                }
            }

            try
            {
                InventoryChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // Guard event dispatch
            }

            return snapshots;
        }
        finally
        {
            _refreshSemaphore.Release();
        }
    }

    public void Dispose()
    {
        _usbProvider.UsbDeviceEvent -= OnUsbDeviceTopologyEvent;
        CancelActiveWait();
        _refreshSemaphore.Dispose();
    }

    private ConnectedDeviceSnapshot EnrichSupportedOps(ConnectedDeviceSnapshot initial)
    {
        var evals = _capabilityService.EvaluateOperationsForDevice(initial);
        var supportedIds = evals.Where(e => e.IsCurrentlyAvailable).Select(e => e.Operation.OperationId).ToList();
        return initial with { SupportedOperationIds = supportedIds };
    }

    private async Task<ConnectedDeviceSnapshot> BuildAdbSnapshotAsync(
        AdbDiscoveredEndpoint adb,
        DetectedUsbDevice? usb,
        CancellationToken ct)
    {
        var interfaces = new List<string> { $"ADB ({adb.State})" };
        if (usb is not null)
            interfaces.Add($"USB {usb.VidPidDisplay}");

        if (adb.State != AdbDeviceState.Connected)
        {
            var mode = adb.State switch
            {
                AdbDeviceState.Unauthorized or AdbDeviceState.Authorizing => ConnectionMode.AdbUnauthorized,
                AdbDeviceState.Offline => ConnectionMode.AdbOffline,
                AdbDeviceState.Recovery => ConnectionMode.Recovery,
                AdbDeviceState.Sideload => ConnectionMode.AdbSideload,
                _ => ConnectionMode.Unknown
            };

            var dashState = adb.State switch
            {
                AdbDeviceState.Unauthorized => ConnectionDashboardState.Unauthorized,
                AdbDeviceState.Authorizing => ConnectionDashboardState.WaitingForAuthorization,
                AdbDeviceState.Sideload or AdbDeviceState.Recovery => ConnectionDashboardState.ProtocolAvailable,
                _ => ConnectionDashboardState.DetectedButUnidentified
            };

            var profileFromHint = _profileRepo.MatchVerifiedProfile(null, adb.ModelHint, adb.DeviceCodenameHint, null);

            return new ConnectedDeviceSnapshot
            {
                DeviceKey = $"ADB:{adb.Serial}",
                TransportSerial = adb.Serial,
                UsbInstanceId = usb?.InstanceId,
                AssociatedComPort = usb?.AssociatedComPort,
                UsbVendorId = usb?.VendorId,
                UsbProductId = usb?.ProductId,
                Manufacturer = profileFromHint is not null
                    ? TrackedValue<string>.Inferred(profileFromHint.Manufacturer, "ADB transport hint")
                    : TrackedValue<string>.Unknown("Authorize ADB to read ro.product.manufacturer"),
                ExactModel = !string.IsNullOrWhiteSpace(adb.ModelHint)
                    ? TrackedValue<string>.Detected(adb.ModelHint, "adb devices -l model hint")
                    : TrackedValue<string>.Unknown("Awaiting ADB authorization"),
                ModelAliases = profileFromHint is not null
                    ? TrackedValue<string>.Inferred(string.Join(", ", profileFromHint.ModelCodes), "Profile match on transport hint")
                    : TrackedValue<string>.Unknown(),
                Codename = !string.IsNullOrWhiteSpace(adb.DeviceCodenameHint)
                    ? TrackedValue<string>.Detected(adb.DeviceCodenameHint, "adb devices -l device hint")
                    : TrackedValue<string>.Unknown(),
                ChipsetFamily = TrackedValue<string>.Unknown("Requires authorized getprop"),
                AndroidVersion = TrackedValue<string>.Unknown("Requires authorized getprop"),
                BuildFingerprint = TrackedValue<string>.Unknown("Requires authorized getprop"),
                BootloaderState = TrackedValue<BootloaderLockState>.Unknown("Requires authorized getprop or fastboot"),
                ActiveConnectionMode = mode,
                DashboardState = dashState,
                AuthorizationStatus = adb.State == AdbDeviceState.Unauthorized
                    ? "Unauthorized — accept RSA prompt on phone screen"
                    : adb.State.ToString(),
                ProtocolProviderStatus = "ADB Transport Active",
                IdentificationConfidence = DataProvenance.Detected,
                MatchedProfileId = profileFromHint?.ProfileId,
                AvailableInterfaces = interfaces,
                LastConnectionEvent = $"ADB endpoint in '{adb.State}' state"
            };
        }

        // Authorized ADB: read real getprop dictionary
        var props = await _adbProvider.GetDevicePropertiesAsync(adb.Serial, ct).ConfigureAwait(false);
        props.TryGetValue("ro.product.manufacturer", out var mfg);
        props.TryGetValue("ro.product.brand", out var brand);
        props.TryGetValue("ro.product.model", out var model);
        props.TryGetValue("ro.product.device", out var codename);
        props.TryGetValue("ro.soc.model", out var socModel);
        props.TryGetValue("ro.board.platform", out var boardPlatform);
        props.TryGetValue("ro.build.version.release", out var androidVer);
        props.TryGetValue("ro.build.version.sdk", out var sdkVer);
        props.TryGetValue("ro.build.fingerprint", out var fingerprint);
        props.TryGetValue("ro.boot.flash.locked", out var flashLocked);

        var effectiveMfg = !string.IsNullOrWhiteSpace(mfg) ? mfg : brand;
        var matchedProfile = _profileRepo.MatchVerifiedProfile(effectiveMfg, model, codename, fingerprint);

        var lockState = flashLocked switch
        {
            "1" => TrackedValue<BootloaderLockState>.Verified(BootloaderLockState.Locked, "ro.boot.flash.locked=1"),
            "0" => TrackedValue<BootloaderLockState>.Verified(BootloaderLockState.Unlocked, "ro.boot.flash.locked=0"),
            _ => TrackedValue<BootloaderLockState>.Unknown("ro.boot.flash.locked not exposed")
        };

        var chipsetText = !string.IsNullOrWhiteSpace(socModel) && !string.IsNullOrWhiteSpace(boardPlatform)
            ? $"{socModel} ({boardPlatform})"
            : socModel ?? boardPlatform;

        return new ConnectedDeviceSnapshot
        {
            DeviceKey = $"ADB:{adb.Serial}",
            TransportSerial = adb.Serial,
            UsbInstanceId = usb?.InstanceId,
            AssociatedComPort = usb?.AssociatedComPort,
            UsbVendorId = usb?.VendorId,
            UsbProductId = usb?.ProductId,
            Manufacturer = !string.IsNullOrWhiteSpace(effectiveMfg)
                ? TrackedValue<string>.Verified(effectiveMfg, "ro.product.manufacturer")
                : TrackedValue<string>.Unknown(),
            ExactModel = matchedProfile is not null
                ? TrackedValue<string>.Verified($"{matchedProfile.MarketingName} ({model ?? matchedProfile.ModelCodes[0]})", "ro.product.model + Verified Profile")
                : !string.IsNullOrWhiteSpace(model)
                    ? TrackedValue<string>.Detected(model, "ro.product.model (No local profile match)")
                    : TrackedValue<string>.Unknown(),
            ModelAliases = matchedProfile is not null
                ? TrackedValue<string>.Verified(string.Join(", ", matchedProfile.ModelCodes), "DeviceProfileRepository")
                : TrackedValue<string>.Unknown("No profile alias record"),
            Codename = !string.IsNullOrWhiteSpace(codename)
                ? TrackedValue<string>.Verified(codename, "ro.product.device")
                : TrackedValue<string>.Unknown(),
            ChipsetFamily = matchedProfile is not null
                ? TrackedValue<string>.Verified($"{matchedProfile.ChipsetFamily} ({matchedProfile.ChipsetCode})", "ro.board.platform + Profile")
                : !string.IsNullOrWhiteSpace(chipsetText)
                    ? TrackedValue<string>.Detected(chipsetText, "ro.soc.model / ro.board.platform")
                    : TrackedValue<string>.Unknown(),
            AndroidVersion = !string.IsNullOrWhiteSpace(androidVer)
                ? TrackedValue<string>.Verified($"Android {androidVer}" + (!string.IsNullOrWhiteSpace(sdkVer) ? $" (API {sdkVer})" : string.Empty), "ro.build.version.release")
                : TrackedValue<string>.Unknown(),
            BuildFingerprint = !string.IsNullOrWhiteSpace(fingerprint)
                ? TrackedValue<string>.Verified(fingerprint, "ro.build.fingerprint")
                : TrackedValue<string>.Unknown(),
            BootloaderState = lockState,
            ActiveConnectionMode = ConnectionMode.AdbAuthorized,
            DashboardState = ConnectionDashboardState.Connected,
            AuthorizationStatus = "Authorized (ADB RSA Session Verified)",
            ProtocolProviderStatus = "ADB Active & Verified",
            IdentificationConfidence = matchedProfile is not null ? DataProvenance.Verified : DataProvenance.Detected,
            MatchedProfileId = matchedProfile?.ProfileId,
            AvailableInterfaces = interfaces,
            RawProperties = props,
            LastConnectionEvent = "Authorized ADB session inspected"
        };
    }

    private async Task<ConnectedDeviceSnapshot> BuildFastbootSnapshotAsync(
        FastbootDiscoveredEndpoint fb,
        DetectedUsbDevice? usb,
        CancellationToken ct)
    {
        var vars = await _fastbootProvider.GetAllVariablesAsync(fb.Serial, ct).ConfigureAwait(false);
        vars.TryGetValue("product", out var product);
        vars.TryGetValue("unlocked", out var unlockedStr);
        vars.TryGetValue("version-bootloader", out var blVer);
        vars.TryGetValue("current-slot", out var slot);

        string? mfgGuess = null;
        if (!string.IsNullOrWhiteSpace(product))
        {
            if (product.Contains("TECNO", StringComparison.OrdinalIgnoreCase)) mfgGuess = "TECNO";
            else if (product.Contains("Infinix", StringComparison.OrdinalIgnoreCase)) mfgGuess = "Infinix";
            else if (product.Contains("itel", StringComparison.OrdinalIgnoreCase)) mfgGuess = "itel";
        }

        var matchedProfile = _profileRepo.MatchVerifiedProfile(mfgGuess, product, product, null);

        var lockState = unlockedStr?.ToLowerInvariant() switch
        {
            "yes" or "true" => TrackedValue<BootloaderLockState>.Verified(BootloaderLockState.Unlocked, "fastboot getvar unlocked=yes"),
            "no" or "false" => TrackedValue<BootloaderLockState>.Verified(BootloaderLockState.Locked, "fastboot getvar unlocked=no"),
            _ => TrackedValue<BootloaderLockState>.Unknown("fastboot getvar unlocked unavailable")
        };

        var interfaces = new List<string> { fb.Mode == ConnectionMode.FastbootdUserspace ? "Fastbootd (Userspace)" : "Fastboot (Bootloader)" };
        if (usb is not null) interfaces.Add($"USB {usb.VidPidDisplay}");
        if (!string.IsNullOrWhiteSpace(slot)) interfaces.Add($"Active Slot: {slot}");

        return new ConnectedDeviceSnapshot
        {
            DeviceKey = $"FASTBOOT:{fb.Serial}",
            TransportSerial = fb.Serial,
            UsbInstanceId = usb?.InstanceId,
            UsbVendorId = usb?.VendorId,
            UsbProductId = usb?.ProductId,
            Manufacturer = matchedProfile is not null
                ? TrackedValue<string>.Verified(matchedProfile.Manufacturer, "Fastboot product + Profile")
                : mfgGuess is not null
                    ? TrackedValue<string>.Detected(mfgGuess, "fastboot getvar product prefix")
                    : TrackedValue<string>.Unknown("Not reported by bootloader"),
            ExactModel = matchedProfile is not null
                ? TrackedValue<string>.Verified(matchedProfile.MarketingName, "fastboot getvar product + Profile")
                : !string.IsNullOrWhiteSpace(product)
                    ? TrackedValue<string>.Detected($"Bootloader product '{product}' (Unverified marketing name)", "fastboot getvar product")
                    : TrackedValue<string>.Unknown(),
            ModelAliases = matchedProfile is not null
                ? TrackedValue<string>.Verified(string.Join(", ", matchedProfile.ModelCodes), "DeviceProfileRepository")
                : TrackedValue<string>.Unknown(),
            Codename = !string.IsNullOrWhiteSpace(product)
                ? TrackedValue<string>.Verified(product, "fastboot getvar product")
                : TrackedValue<string>.Unknown(),
            ChipsetFamily = matchedProfile is not null
                ? TrackedValue<string>.Inferred($"{matchedProfile.ChipsetFamily} ({matchedProfile.ChipsetCode})", "Matched Device Profile")
                : TrackedValue<string>.Unknown("Bootloader does not expose SoC marketing string"),
            AndroidVersion = fb.Mode == ConnectionMode.FastbootdUserspace
                ? TrackedValue<string>.Inferred("Android 10–16 (Fastbootd userspace active)", "is-userspace=yes")
                : TrackedValue<string>.Unknown("Not available in bootloader mode"),
            BuildFingerprint = !string.IsNullOrWhiteSpace(blVer)
                ? TrackedValue<string>.Detected($"BL: {blVer}", "version-bootloader")
                : TrackedValue<string>.Unknown("OS not booted"),
            BootloaderState = lockState,
            ActiveConnectionMode = fb.Mode,
            DashboardState = ConnectionDashboardState.ProtocolAvailable,
            AuthorizationStatus = lockState.Value == BootloaderLockState.Unlocked
                ? "Bootloader Unlocked (Write operations permitted)"
                : "Bootloader Locked (Read-only / Official Unlock only)",
            ProtocolProviderStatus = $"{fb.Mode} Ready",
            IdentificationConfidence = matchedProfile is not null ? DataProvenance.Verified : DataProvenance.Detected,
            MatchedProfileId = matchedProfile?.ProfileId,
            AvailableInterfaces = interfaces,
            RawProperties = vars,
            LastConnectionEvent = $"Enumerated in {fb.Mode}"
        };
    }

    private static ConnectedDeviceSnapshot BuildUsbOnlySnapshot(DetectedUsbDevice usb)
    {
        // Crucial engineering guardrail (Section 3):
        // NEVER infer a phone's exact model from USB VID/PID or chipset family alone!
        var chipsetTracked = usb.DetectedChipsetVendor switch
        {
            ChipsetVendor.MediaTek => TrackedValue<string>.Detected("MediaTek USB Interface (Run BROM Probe for HW_CODE)", $"USB VID 0x{usb.VendorId:X4}"),
            ChipsetVendor.Unisoc => TrackedValue<string>.Detected("Unisoc / Spreadtrum USB Interface", $"USB VID 0x{usb.VendorId:X4}"),
            ChipsetVendor.Qualcomm => TrackedValue<string>.Detected("Qualcomm USB Diagnostic Interface", $"USB VID 0x{usb.VendorId:X4}"),
            _ => TrackedValue<string>.Unknown("Cannot determine SoC from generic USB descriptor")
        };

        var dashState = usb.DriverDiagnostics.Status != DriverHealthStatus.Healthy
            ? ConnectionDashboardState.ProtocolUnsupported
            : usb.ClassifiedMode is ConnectionMode.MediaTekBrom or ConnectionMode.MediaTekPreloader or ConnectionMode.UnisocDownload
                ? ConnectionDashboardState.DetectedButUnidentified
                : usb.ClassifiedMode == ConnectionMode.MediaTekMeta
                    ? ConnectionDashboardState.AuthenticationRequired
                    : ConnectionDashboardState.DetectedButUnidentified;

        var interfaces = new List<string> { $"USB {usb.VidPidDisplay} ({usb.FriendlyName})" };
        if (!string.IsNullOrWhiteSpace(usb.AssociatedComPort))
            interfaces.Add($"Serial {usb.AssociatedComPort}");

        return new ConnectedDeviceSnapshot
        {
            DeviceKey = $"USB:{usb.InstanceId}",
            TransportSerial = usb.SerialNumber ?? usb.AssociatedComPort,
            UsbInstanceId = usb.InstanceId,
            AssociatedComPort = usb.AssociatedComPort,
            UsbVendorId = usb.VendorId,
            UsbProductId = usb.ProductId,
            Manufacturer = usb.VendorId == Usb.UsbSignatureCatalog.VidTranssion
                ? TrackedValue<string>.Detected("Transsion Holdings (TECNO / Infinix / itel)", "USB VID 0x2ECC")
                : TrackedValue<string>.Detected(usb.ManufacturerLabel, "Windows PnP Descriptor"),
            ExactModel = TrackedValue<string>.Unknown("Unidentified in low-level USB mode (Exact model is never inferred from chipset or VID/PID)"),
            ModelAliases = TrackedValue<string>.Unknown(),
            Codename = TrackedValue<string>.Unknown(),
            ChipsetFamily = chipsetTracked,
            AndroidVersion = TrackedValue<string>.Unknown("OS not active on low-level USB endpoint"),
            BuildFingerprint = TrackedValue<string>.Unknown(),
            BootloaderState = TrackedValue<BootloaderLockState>.Unknown("Not queryable from raw USB descriptor"),
            ActiveConnectionMode = usb.ClassifiedMode,
            DashboardState = dashState,
            AuthorizationStatus = usb.DriverDiagnostics.Status != DriverHealthStatus.Healthy
                ? usb.DriverDiagnostics.Summary
                : usb.ClassifiedMode is ConnectionMode.MediaTekBrom or ConnectionMode.MediaTekPreloader
                    ? "SLA/DAA hardware status unknown until Read-Only BROM Probe runs"
                    : usb.ClassifiedMode == ConnectionMode.UnisocDownload
                        ? "Signed FDL1/FDL2 required for write workflows"
                        : "USB Descriptor Detected",
            ProtocolProviderStatus = usb.DriverDiagnostics.Status == DriverHealthStatus.Healthy
                ? $"{usb.ClassifiedMode} Interface Detected"
                : $"Driver Issue ({usb.DriverDiagnostics.Status})",
            IdentificationConfidence = DataProvenance.Detected,
            MatchedProfileId = null,
            AvailableInterfaces = interfaces,
            LastConnectionEvent = $"USB PnP detected: {usb.FriendlyName}"
        };
    }
}
