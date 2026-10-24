using Microsoft.Extensions.DependencyInjection;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Firmware;
using TranssionLab.Protocols.Discovery;
using TranssionLab.Protocols.Transport;
using TranssionLab.Tests.Mocks;
using TranssionLab.ViewModels;
using TranssionLab.ViewModels.Mvvm;
using TranssionLab.ViewModels.Pages;
using Xunit;

namespace TranssionLab.Tests;

[Trait("Category", "SimulatedUnit")]
public sealed class WaitingForDeviceAndStabilityTests : IDisposable
{
    private readonly string _tempDir;

    public WaitingForDeviceAndStabilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"TranssionLab_WaitTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        UiInteractionBridge.InvokeOnUiThread = null;
        UiInteractionBridge.OnCommandException = null;
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task PartitionManagerScan_ShowsWaitingForDeviceWhenUnplugged_AndCompletesAutomaticallyWhenConnected()
    {
        var phoneAttached = 0; // 0 = unplugged, 1 = plugged in fastbootd
        var observedWaitingMessages = new List<string>();

        var pnp = new SimulatedPnpBackend();
        var runner = new RecordedProcessRunner((exe, args) =>
        {
            bool isFastboot = exe.Contains("fastboot", StringComparison.OrdinalIgnoreCase);
            bool isAdb = exe.Contains("adb", StringComparison.OrdinalIgnoreCase);

            if (isAdb)
                return new CommandExecutionResult(true, 0, "List of devices attached\n\n", "", 1);

            if (!isFastboot || Volatile.Read(ref phoneAttached) == 0)
                return new CommandExecutionResult(true, 0, "", "", 1);

            if (args.Contains("devices"))
                return new CommandExecutionResult(true, 0, "TECNO_CL9_WAIT\tfastboot\n", "", 1);

            if (args.Count >= 2 && args[0] == "getvar" && args[1] == "is-userspace")
                return new CommandExecutionResult(true, 0, "", "is-userspace: yes\nFinished.", 1);

            if (args.Count >= 3 && args[0] == "-s" && args[2] == "getvar" && args[3] == "is-userspace")
                return new CommandExecutionResult(true, 0, "", "is-userspace: yes\nFinished.", 1);

            if (args.Contains("all"))
            {
                return new CommandExecutionResult(true, 0, "",
                    "(bootloader) product: TECNO-CL9\n" +
                    "(bootloader) is-userspace: yes\n" +
                    "(bootloader) unlocked: yes\n" +
                    "(bootloader) current-slot: a\n" +
                    "(bootloader) partition-size:boot_a: 0x4000000\n" +
                    "(bootloader) partition-size:system_a: 0x80000000\n" +
                    "(bootloader) is-logical:system_a: yes\n" +
                    "(bootloader) partition-size:nvram: 0x4000000\n" +
                    "Finished.", 2);
            }

            return new CommandExecutionResult(true, 0, "", "OKAY", 1);
        });

        using var provider = BuildTestContainer(pnp, runner, pollMs: 25, timeoutMs: 3000);
        var shell = provider.GetRequiredService<MainShellViewModel>();
        var partVm = provider.GetRequiredService<PartitionManagerViewModel>();
        var discovery = provider.GetRequiredService<IDeviceDiscoveryProvider>();

        discovery.WaitingStatusChanged += (_, msg) =>
        {
            if (!string.IsNullOrWhiteSpace(msg))
            {
                lock (observedWaitingMessages)
                    observedWaitingMessages.Add(msg);
            }
        };

        shell.CurrentPage = partVm;

        // Launch Scan while NO phone is plugged in
        var scanTask = partVm.ScanPartitionsCommand.ExecuteAsync();

        // Wait until the UI enters "Waiting for device..." state
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!shell.IsWaitingForDevice && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(shell.IsWaitingForDevice);
        Assert.Contains("Waiting for device...", shell.WaitingBannerMessage ?? string.Empty);
        Assert.Contains("Scan Device Partitions", partVm.StatusBanner);

        // Simulate plugging in the phone over USB in Fastbootd mode
        Interlocked.Exchange(ref phoneAttached, 1);

        await scanTask;

        Assert.False(shell.IsWaitingForDevice);
        Assert.False(partVm.IsBusy);
        Assert.NotNull(partVm.CurrentSnapshot);
        Assert.Equal(3, partVm.CurrentSnapshot!.TotalPartitions);
        Assert.NotEmpty(observedWaitingMessages);
        Assert.Contains(observedWaitingMessages, m => m.StartsWith("Waiting for device...", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdbTelemetryActivity_WaitsForRsaAuthorization_AndCompletesOnceAuthorized()
    {
        var adbState = "unauthorized";
        var pnp = new SimulatedPnpBackend();
        var runner = new RecordedProcessRunner((exe, args) =>
        {
            if (!exe.Contains("adb", StringComparison.OrdinalIgnoreCase))
                return new CommandExecutionResult(true, 0, "", "", 1);

            if (args.Contains("version"))
                return new CommandExecutionResult(true, 0, "Android Debug Bridge version 1.0.41", "", 1);

            if (args.Contains("devices"))
            {
                var st = Volatile.Read(ref adbState);
                return new CommandExecutionResult(true, 0, $"List of devices attached\nINFINIX_GT20\t{st} model:Infinix_X6871 device:Infinix-X6871\n", "", 1);
            }

            if (args.Contains("getprop"))
            {
                return new CommandExecutionResult(true, 0,
                    "[ro.product.manufacturer]: [Infinix]\n[ro.product.model]: [Infinix X6871]\n[ro.build.version.release]: [15]\n", "", 1);
            }

            if (args.Contains("dumpsys") && args.Contains("battery"))
                return new CommandExecutionResult(true, 0, "  level: 84\n  status: 2", "", 1);

            if (args.Contains("df") && args.Contains("-h"))
                return new CommandExecutionResult(true, 0, "/data 220G 40G 180G 19%", "", 1);

            return new CommandExecutionResult(true, 0, "", "", 1);
        });

        using var provider = BuildTestContainer(pnp, runner, pollMs: 25, timeoutMs: 3000);
        var shell = provider.GetRequiredService<MainShellViewModel>();
        var adbVm = provider.GetRequiredService<AdbManagerViewModel>();
        shell.CurrentPage = adbVm;

        var telemetryTask = adbVm.ReadBatteryAndStorageCommand.ExecuteAsync();

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!shell.IsWaitingForDevice && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(shell.IsWaitingForDevice);
        Assert.Contains("Allow USB debugging", shell.WaitingBannerMessage ?? string.Empty);

        // User taps "Allow" on handset screen
        Volatile.Write(ref adbState, "device");

        await telemetryTask;

        Assert.False(shell.IsWaitingForDevice);
        Assert.Contains("level: 84", adbVm.DiagnosticOutput);
        Assert.Contains("/data 220G", adbVm.DiagnosticOutput);
    }

    [Fact]
    public async Task CancelWaitingCommand_AbortsDeviceWaitCleanly_WithZeroExceptions()
    {
        var unexpectedErrors = new List<Exception>();
        UiInteractionBridge.OnCommandException = ex => unexpectedErrors.Add(ex);

        var pnp = new SimulatedPnpBackend();
        var runner = new RecordedProcessRunner((_, _) => new CommandExecutionResult(true, 0, "", "", 1));

        using var provider = BuildTestContainer(pnp, runner, pollMs: 30, timeoutMs: 10000);
        var shell = provider.GetRequiredService<MainShellViewModel>();
        var fbVm = provider.GetRequiredService<FastbootManagerViewModel>();
        shell.CurrentPage = fbVm;

        var queryTask = fbVm.QueryVariablesCommand.ExecuteAsync();

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!shell.IsWaitingForDevice && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(shell.IsWaitingForDevice);
        Assert.True(shell.CancelWaitingCommand.CanExecute(null));

        // User clicks "Cancel Waiting" in the top bar
        shell.CancelWaitingCommand.Execute(null);

        await queryTask;

        Assert.False(shell.IsWaitingForDevice);
        Assert.False(fbVm.IsBusy);
        Assert.Contains("Stopped waiting for device", fbVm.StatusBanner);
        Assert.Empty(unexpectedErrors);
    }

    [Fact]
    public async Task MediaTekBromProbe_WaitsForTransientComPortArrival_AndExecutesReadOnlyHandshake()
    {
        var pnp = new SimulatedPnpBackend();
        var runner = new RecordedProcessRunner((_, _) => new CommandExecutionResult(true, 0, "", "", 1));
        var serialFactory = new SimulatedSerialChannelFactory((_, tx) =>
        {
            if (tx.Length == 1)
            {
                return tx[0] switch
                {
                    0xA0 => [0x5F],
                    0x0A => [0xF5],
                    0x50 => [0xAF],
                    0x05 => [0xFA],
                    0xFD => [0xFD, 0x67, 0x89, 0x00, 0x00], // MT6789 Helio G99
                    0xFC => [0xFC, 0x8A, 0x00, 0xCA, 0x00, 0x00, 0x00, 0x00, 0x00],
                    0xD8 => [0xD8, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00], // SBC + SLA + DAA
                    _ => []
                };
            }
            return [];
        });

        using var provider = BuildTestContainer(pnp, runner, pollMs: 25, timeoutMs: 3000, serialFactory: serialFactory);
        var shell = provider.GetRequiredService<MainShellViewModel>();
        var mtkVm = provider.GetRequiredService<MediaTekToolsViewModel>();
        shell.CurrentPage = mtkVm;

        Assert.Equal(string.Empty, mtkVm.SelectedComPort);

        var probeTask = mtkVm.ProbeReadOnlyBromCommand.ExecuteAsync();

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!shell.IsWaitingForDevice && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(shell.IsWaitingForDevice);
        Assert.Contains("MediaTek BROM / Preloader Probe", shell.WaitingBannerMessage ?? string.Empty);

        // Technician plugs in powered-off Helio G99 handset -> Windows enumerates VID_0E8D&PID_0003 (COM7)
        pnp.Mutate(b =>
        {
            b.Devices.Add(new RawPnpDeviceRecord(
                @"USB\VID_0E8D&PID_0003\6&2A3B4C5D&0&2",
                "MediaTek USB Port (COM7)",
                "MediaTek Inc.",
                "usbser",
                0,
                "OK"));
            b.ComPorts.Add("COM7");
        });

        await probeTask;

        Assert.False(shell.IsWaitingForDevice);
        Assert.Equal("COM7", mtkVm.SelectedComPort);
        Assert.NotNull(mtkVm.LastProbeResult);
        Assert.True(mtkVm.LastProbeResult!.SyncSucceeded);
        Assert.Equal((ushort)0x6789, mtkVm.LastProbeResult.HardwareCode);
        Assert.True(mtkVm.LastProbeResult.SerialLinkAuthRequired);
    }

    [Fact]
    public async Task All19Pages_EndToEndNavigation_OfflineLayout_WirelessSerialSanitization_AndFaultResilience_NeverCrash()
    {
        var marshalledCount = 0;
        UiInteractionBridge.InvokeOnUiThread = action =>
        {
            Interlocked.Increment(ref marshalledCount);
            action();
        };

        var pnp = new SimulatedPnpBackend();
        pnp.Mutate(b =>
        {
            b.Devices.Add(new RawPnpDeviceRecord(@"USB\VID_2ECC&PID_2001\DEV_ONE", "TECNO ADB Interface", "TECNO", "WinUSB", 0, "OK"));
            b.Devices.Add(new RawPnpDeviceRecord(@"USB\VID_2ECC&PID_2001\DEV_TWO", "Infinix ADB Interface", "Infinix", "WinUSB", 0, "OK"));
        });

        var runner = new RecordedProcessRunner((exe, args) =>
        {
            if (exe.Contains("adb", StringComparison.OrdinalIgnoreCase) && args.Contains("devices"))
            {
                // Wireless ADB serial with colon ':' to verify filename sanitization!
                return new CommandExecutionResult(true, 0, "List of devices attached\n192.168.1.50:5555\tdevice\n192.168.1.51:5555\tdevice\n", "", 1);
            }
            return new CommandExecutionResult(false, -99, "", "Simulated binary missing or I/O failure", 1);
        });

        using var provider = BuildTestContainer(pnp, runner, pollMs: 20, timeoutMs: 250);
        var shell = provider.GetRequiredService<MainShellViewModel>();
        var logger = provider.GetRequiredService<IStructuredLogger>();
        var partVm = provider.GetRequiredService<PartitionManagerViewModel>();

        // 1. Navigate across every single one of the 19 pages & verify zero crashes
        await shell.GlobalScanDevicesCommand.ExecuteAsync();
        Assert.True(shell.RequiresExplicitSelectionBanner);

        foreach (var page in shell.Pages)
        {
            shell.CurrentPage = page;
            await page.OnNavigatedToAsync();
        }

        // 2. Multi-device ambiguity check during WaitForDeviceAsync must block cleanly (never pick silently)
        await partVm.ScanPartitionsCommand.ExecuteAsync();
        Assert.Contains("Multiple devices", partVm.StatusBanner, StringComparison.OrdinalIgnoreCase);

        // 3. Offline scatter layout loading & JSON export in Partition Manager
        var scatterPath = Path.Combine(_tempDir, "MT6789_Android_scatter.txt");
        await File.WriteAllTextAsync(scatterPath, """
            - general: MTK_PLATFORM_CFG
              info:
                - config_version: V1.1.2
                  platform: MT6789
                  project: TECNO-CL6
                  storage: UFS
            - partition_index: SYS0
              partition_name: preloader
              file_name: preloader.bin
              partition_size: 0x80000
            - partition_index: SYS1
              partition_name: boot_a
              file_name: boot.img
              partition_size: 0x4000000
            - partition_index: SYS2
              partition_name: nvram
              file_name: nvram.bin
              partition_size: 0x4000000
            - partition_index: SYS3
              partition_name: frp
              file_name: frp.bin
              partition_size: 0x100000
            """);

        await partVm.LoadOfflineFromPathAsync(scatterPath);
        Assert.NotNull(partVm.CurrentSnapshot);
        Assert.Equal(4, partVm.CurrentSnapshot!.TotalPartitions);
        Assert.Equal(3, partVm.CurrentSnapshot.ProtectedPartitionsCount); // preloader + nvram + frp

        // 4. Wireless ADB serial filename sanitization check
        var sanitized = HashAndBinaryHelpers.SanitizeFileNameComponent("192.168.1.50:5555");
        Assert.DoesNotContain(":", sanitized);
        Assert.Equal("192.168.1.50_5555", sanitized);

        // 5. Concurrent background thread logging & PnP events do not crash UI subscriptions
        var bgTasks = Enumerable.Range(0, 8).Select(idx => Task.Run(() =>
        {
            logger.LogInfo("BgStress", $"Concurrent log #{idx}");
            pnp.TriggerTopologyChange();
        }));
        await Task.WhenAll(bgTasks);

        Assert.True(marshalledCount > 0);
    }

    private ServiceProvider BuildTestContainer(
        SimulatedPnpBackend pnp,
        RecordedProcessRunner runner,
        int pollMs,
        int timeoutMs,
        ISerialChannelFactory? serialFactory = null)
    {
        var services = new ServiceCollection();
        services.AddTranssionLabServices(
            customDataDirectory: Path.Combine(_tempDir, Guid.NewGuid().ToString("N")),
            customPnpBackend: pnp,
            customProcessRunner: runner,
            customSerialFactory: serialFactory ?? new SimulatedSerialChannelFactory((_, _) => []));

        var sp = services.BuildServiceProvider();
        if (sp.GetRequiredService<IDeviceDiscoveryProvider>() is DeviceDiscoveryProvider concrete)
        {
            concrete.PollInterval = TimeSpan.FromMilliseconds(pollMs);
            concrete.VendorPollInterval = TimeSpan.FromMilliseconds(pollMs);
            concrete.DefaultWaitTimeout = TimeSpan.FromMilliseconds(timeoutMs);
        }
        return sp;
    }
}
