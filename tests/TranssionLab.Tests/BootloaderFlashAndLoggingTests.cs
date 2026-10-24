using Microsoft.Extensions.DependencyInjection;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Core.Services;
using TranssionLab.Firmware;
using TranssionLab.Logging;
using TranssionLab.Profiles;
using TranssionLab.Protocols.Android;
using TranssionLab.Protocols.Diagnostics;
using TranssionLab.Protocols.Discovery;
using TranssionLab.Protocols.MediaTek;
using TranssionLab.Protocols.Unisoc;
using TranssionLab.Protocols.Usb;
using TranssionLab.Servicing;
using TranssionLab.Tests.Mocks;
using TranssionLab.ViewModels;
using Xunit;

namespace TranssionLab.Tests;

[Trait("Category", "SimulatedUnit")]
public sealed class BootloaderFlashAndLoggingTests : IDisposable
{
    private readonly string _tempDir;

    public BootloaderFlashAndLoggingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "transsionlab_svc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task BootloaderManager_EnforcesConfirmationPhrase_AndVerifiesActualPostUnlockState()
    {
        bool unlockedOnDevice = false;
        var runner = new RecordedProcessRunner((exe, args) =>
        {
            if (exe == "adb")
                return new CommandExecutionResult(true, 0, "List of devices attached\n", "", 1);
            var cmd = string.Join(' ', args);
            if (cmd == "devices -l")
                return new CommandExecutionResult(true, 0, "CL9FASTBOOT\tfastboot\n", "", 3);
            if (cmd.Contains("getvar is-userspace"))
                return new CommandExecutionResult(true, 0, "", "is-userspace: no\n", 2);
            if (cmd.Contains("getvar product"))
                return new CommandExecutionResult(true, 0, "", "product: TECNO-CL9\n", 2);
            if (cmd.Contains("getvar all"))
                return new CommandExecutionResult(true, 0, "", $"product: TECNO-CL9\nunlocked: {(unlockedOnDevice ? "yes" : "no")}\nis-userspace: no\n", 3);
            if (cmd.Contains("getvar unlocked"))
                return new CommandExecutionResult(true, 0, "", $"unlocked: {(unlockedOnDevice ? "yes" : "no")}\n", 2);
            if (cmd.Contains("flashing get_unlock_ability"))
                return new CommandExecutionResult(true, 0, "", "(bootloader) get_unlock_ability: 1\nOKAY", 3);
            if (cmd == "-s CL9FASTBOOT flashing unlock")
            {
                unlockedOnDevice = true;
                return new CommandExecutionResult(true, 0, "", "OKAY [ 4.120s ]\nFinished.", 4120);
            }
            return new CommandExecutionResult(true, 0, "", "", 1);
        });

        var settings = new AppSettingsService(_tempDir);
        var logger = new StructuredLogger(Path.Combine(_tempDir, "logs"));
        var history = new OperationHistoryStore(Path.Combine(_tempDir, "history"));
        var profiles = new DeviceProfileRepository();
        var pnp = new SimulatedPnpBackend();
        var serial = new SimulatedSerialChannelFactory((_, _) => []);

        var usb = new UsbInterfaceProvider(pnp, logger);
        var adb = new AdbProvider(runner, settings, logger);
        var rec = new RecoveryProvider(adb);
        var fb = new FastbootProvider(runner, settings, logger);
        var cap = new ProtocolCapabilityService(usb, adb, rec, fb, new MediaTekProvider(serial, logger), new MetaModeProvider(settings, logger), new SpdUnisocProvider(serial, logger), new DiagnosticProvider(serial, logger), new OperationRegistry());
        var discovery = new DeviceDiscoveryProvider(usb, adb, fb, profiles, cap, logger);
        var blManager = new BootloaderManager(profiles, fb, adb, logger, history);

        await discovery.RefreshInventoryAsync();
        var dev = discovery.RequireExplicitlySelectedDevice();
        var assessment = await blManager.AssessDeviceAsync(dev);

        Assert.True(assessment.IsUnlockWorkflowPermitted);
        Assert.True(assessment.Android16CaveatApplies);
        Assert.Equal("UNLOCK CL9FASTBOOT ERASE-DATA", assessment.ExpectedConfirmationPhrase);

        // Wrong phrase must throw SafetyPolicyViolationException
        await Assert.ThrowsAsync<SafetyPolicyViolationException>(() =>
            blManager.ExecuteVerifiedUnlockAsync(dev, assessment, "WRONG PHRASE"));

        // Exact phrase executes and verifies post-unlock state == Unlocked
        var res = await blManager.ExecuteVerifiedUnlockAsync(dev, assessment, "UNLOCK CL9FASTBOOT ERASE-DATA");
        Assert.True(res.VerifiedStateMatched);
        Assert.Equal(BootloaderLockState.Unlocked, res.ResultingState);
        Assert.Single(history.GetRecords());
    }

    [Fact]
    public async Task FlashManager_EnforcesAll11Steps_BlocksProtectedAndWrongModeLogicalPartitions_NeverWipesUserdata()
    {
        var runner = new RecordedProcessRunner((exe, args) =>
        {
            if (exe == "adb")
                return new CommandExecutionResult(true, 0, "List of devices attached\n", "", 1);
            var cmd = string.Join(' ', args);
            if (cmd == "--version")
                return new CommandExecutionResult(true, 0, "fastboot version 35.0.2-android", "", 2);
            if (cmd == "devices -l")
                return new CommandExecutionResult(true, 0, "CL9FLASH01\tfastboot\n", "", 2);
            if (cmd.Contains("getvar is-userspace"))
                return new CommandExecutionResult(true, 0, "", "is-userspace: no\n", 2);
            if (cmd.Contains("getvar all"))
                return new CommandExecutionResult(true, 0, "", "product: TECNO-CL9\nunlocked: yes\nis-userspace: no\ncurrent-slot: a\n", 2);
            if (cmd.Contains("getvar unlocked"))
                return new CommandExecutionResult(true, 0, "", "unlocked: yes\n", 2);
            if (cmd.Contains("getvar partition-size:boot_a"))
                return new CommandExecutionResult(true, 0, "", "partition-size:boot_a: 0x4000000\n", 2);
            if (cmd.Contains("getvar current-slot"))
                return new CommandExecutionResult(true, 0, "", "current-slot: a\n", 2);
            if (cmd.Contains("flash boot_a"))
                return new CommandExecutionResult(true, 0, "", "Sending 'boot_a' (4 KB) OKAY\nWriting 'boot_a' OKAY\nFinished.", 25);
            return new CommandExecutionResult(true, 0, "", "", 1);
        });

        var settings = new AppSettingsService(_tempDir);
        var logger = new StructuredLogger(Path.Combine(_tempDir, "logs"));
        var history = new OperationHistoryStore(Path.Combine(_tempDir, "history"));
        var profiles = new DeviceProfileRepository();
        var pnp = new SimulatedPnpBackend();
        var serial = new SimulatedSerialChannelFactory((_, _) => []);

        var usb = new UsbInterfaceProvider(pnp, logger);
        var adb = new AdbProvider(runner, settings, logger);
        var rec = new RecoveryProvider(adb);
        var fb = new FastbootProvider(runner, settings, logger);
        var cap = new ProtocolCapabilityService(usb, adb, rec, fb, new MediaTekProvider(serial, logger), new MetaModeProvider(settings, logger), new SpdUnisocProvider(serial, logger), new DiagnosticProvider(serial, logger), new OperationRegistry());
        var discovery = new DeviceDiscoveryProvider(usb, adb, fb, profiles, cap, logger);
        var fwRegistry = new FirmwareFormatRegistry(profiles, logger);
        var flashMgr = new FlashManager(discovery, profiles, fb, fwRegistry, settings, logger, history);

        await discovery.RefreshInventoryAsync();
        var dev = discovery.RequireExplicitlySelectedDevice();

        var bootImg = Path.Combine(_tempDir, "boot_a.img");
        await File.WriteAllBytesAsync(bootImg, new byte[4096]);

        // Case A: Protected partition nvram MUST be blocked
        var blockedNvPlan = await flashMgr.BuildExecutionPlanAsync(dev, [("nvram", bootImg)]);
        Assert.True(blockedNvPlan.BlockedBySafetyPolicy);

        // Case B: Logical partition system_a in Bootloader Fastboot (not Fastbootd) MUST be blocked
        var blockedLogicalPlan = await flashMgr.BuildExecutionPlanAsync(dev, [("system_a", bootImg)]);
        Assert.True(blockedLogicalPlan.BlockedBySafetyPolicy);

        // Case C: Valid physical partition boot_a succeeds through all 11 steps without touching userdata
        var validPlan = await flashMgr.BuildExecutionPlanAsync(dev, [("boot_a", bootImg)]);
        Assert.False(validPlan.BlockedBySafetyPolicy);
        Assert.False(validPlan.TouchesUserdata);

        var dryRun = await flashMgr.RunDryRunValidationAsync(validPlan);
        Assert.True(dryRun.Passed);

        var confirm = new FlashConfirmationToken(
            validPlan.PlanId,
            validPlan.PlanHashSha256,
            "FLASH CL9FLASH01",
            UserAcknowledgedDataAndBrickRisk: true,
            DateTimeOffset.UtcNow);

        var report = await flashMgr.ExecutePlanAsync(validPlan, dryRun, confirm);
        Assert.True(report.Succeeded);
        Assert.True(report.PostStateVerified);
        Assert.True(File.Exists(report.SavedReportPath));
        // Ensure '-w' or 'erase userdata' was NEVER invoked
        Assert.DoesNotContain(runner.Invocations, inv => inv.Args.Contains("-w") || inv.Args.Contains("userdata"));
    }

    [Fact]
    public async Task StructuredLogger_RedactsSecrets_PreservesArchiveOnClearDisplay_AndExportsFiles()
    {
        var logger = new StructuredLogger(Path.Combine(_tempDir, "logs_redact"));
        logger.LogInfo(
            "TestProvider",
            "upload --token=SUPER_SECRET_12345",
            "-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA...\n-----END RSA PRIVATE KEY-----\nAuthorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig");

        var displayed = logger.GetDisplayEntries();
        Assert.Single(displayed);
        Assert.DoesNotContain("SUPER_SECRET_12345", displayed[0].RequestedAction);
        Assert.Contains("[REDACTED]", displayed[0].RequestedAction);
        Assert.DoesNotContain("MIIEpAIBAAKCAQEA", displayed[0].CommandOutput);
        Assert.Contains("[REDACTED_PRIVATE_KEY]", displayed[0].CommandOutput);
        Assert.Contains("[REDACTED_TOKEN]", displayed[0].CommandOutput);

        // ClearDisplayOnly clears live buffer without deleting session archive
        logger.ClearDisplayOnly();
        Assert.Empty(logger.GetDisplayEntries());
        Assert.Single(logger.GetArchivedSessionEntries());

        var txtOut = Path.Combine(_tempDir, "export.txt");
        var jsonOut = Path.Combine(_tempDir, "export.json");
        await logger.ExportTxtAsync(txtOut);
        await logger.ExportJsonAsync(jsonOut);
        Assert.True(File.Exists(txtOut));
        Assert.True(File.Exists(jsonOut));
    }

    [Fact]
    public void DependencyInjectionAndShellViewModel_RegistersAll18PagesInExactOrder()
    {
        var services = new ServiceCollection();
        services.AddTranssionLabServices(
            customDataDirectory: Path.Combine(_tempDir, "di_data"),
            customPnpBackend: new SimulatedPnpBackend(),
            customProcessRunner: new RecordedProcessRunner((_, _) => new CommandExecutionResult(false, -1, "", "", 1)),
            customSerialFactory: new SimulatedSerialChannelFactory((_, _) => []));

        using var provider = services.BuildServiceProvider();
        var shell = provider.GetRequiredService<MainShellViewModel>();

        Assert.Equal(18, shell.Pages.Count);
        for (int i = 0; i < 18; i++)
        {
            Assert.Equal(i + 1, shell.Pages[i].PageNumber);
        }
    }
}
