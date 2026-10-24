using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Services;
using TranssionLab.Logging;
using TranssionLab.Profiles;
using TranssionLab.Protocols.Android;
using TranssionLab.Protocols.Diagnostics;
using TranssionLab.Protocols.Discovery;
using TranssionLab.Protocols.MediaTek;
using TranssionLab.Protocols.Unisoc;
using TranssionLab.Protocols.Usb;
using TranssionLab.Tests.Mocks;
using TranssionLab.ViewModels.Pages;
using Xunit;

namespace TranssionLab.Tests;

[Trait("Category", "SimulatedUnit")]
public sealed class DeviceProfileAndViewModelCapabilityTests : IDisposable
{
    private readonly string _tempDir;

    public DeviceProfileAndViewModelCapabilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "transsionlab_cap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ProfileRepository_ValidatesHardwareVerificationIntegrity_AndImportsValidJsonProfile()
    {
        var repo = new DeviceProfileRepository(Path.Combine(_tempDir, "profiles"));
        var seed = repo.GetById("tecno-camon30-premier-cl9")!;

        // Falsely claiming HardwareVerified without IsHardwareVerified=true + TestedBuildFingerprint must fail validation
        var invalidFakeVerified = seed with
        {
            ProfileId = "fake-verified-test",
            VerificationLevel = HardwareVerificationLevel.HardwareVerified
        };
        var errors = repo.ValidateProfile(invalidFakeVerified);
        Assert.NotEmpty(errors);

        // Properly documented bench-tested profile imports cleanly
        var validCustom = seed with
        {
            ProfileId = "tecno-camon30-premier-cl9-bench01",
            VerificationLevel = HardwareVerificationLevel.HardwareVerified,
            HardwareVerification = new(
                IsHardwareVerified: true,
                TestedBy: "QA Lab Bench",
                TestedAtUtc: DateTimeOffset.UtcNow,
                TestedBuildFingerprint: "TECNO/CL9-OP/TECNO-CL9:16/BP2A.250605.031/261001:user/release-keys",
                VerificationNotes: "Verified ADB getprop and Fastbootd variables on physical bench unit.")
        };

        var jsonPath = Path.Combine(_tempDir, "bench_profile.json");
        await File.WriteAllTextAsync(jsonPath, DeviceProfileRepository.SerializeProfile(validCustom));

        var imported = await repo.ImportProfileFromFileAsync(jsonPath);
        Assert.Equal("tecno-camon30-premier-cl9-bench01", imported.ProfileId);
        Assert.Equal(HardwareVerificationLevel.HardwareVerified, imported.VerificationLevel);
    }

    [Fact]
    public void ProfileMatcher_NeverMatchesOnChipsetAlone()
    {
        var repo = new DeviceProfileRepository();
        // Passing null model/codename/fingerprint even for TECNO must return null — never guess model!
        var match = repo.MatchVerifiedProfile("TECNO", null, null, null);
        Assert.Null(match);
    }

    [Fact]
    public async Task DashboardViewModel_DisplaysOnlyPermittedOperationsForCurrentModeAndLockState()
    {
        bool unlocked = false;
        var runner = new RecordedProcessRunner((exe, args) =>
        {
            if (exe == "adb")
                return new CommandExecutionResult(true, 0, "List of devices attached\n", "", 1);
            var cmd = string.Join(' ', args);
            if (cmd == "devices -l")
                return new CommandExecutionResult(true, 0, "X6871FB\tfastboot\n", "", 2);
            if (cmd.Contains("getvar is-userspace"))
                return new CommandExecutionResult(true, 0, "", "is-userspace: no\n", 1);
            if (cmd.Contains("getvar all"))
                return new CommandExecutionResult(true, 0, "", $"product: Infinix-X6871\nunlocked: {(unlocked ? "yes" : "no")}\n", 2);
            return new CommandExecutionResult(true, 0, "", "", 1);
        });

        var settings = new AppSettingsService(_tempDir);
        var logger = new StructuredLogger(Path.Combine(_tempDir, "logs"));
        var pnp = new SimulatedPnpBackend();
        var serial = new SimulatedSerialChannelFactory((_, _) => []);
        var usb = new UsbInterfaceProvider(pnp, logger);
        var adb = new AdbProvider(runner, settings, logger);
        var rec = new RecoveryProvider(adb);
        var fb = new FastbootProvider(runner, settings, logger);
        var cap = new ProtocolCapabilityService(usb, adb, rec, fb, new MediaTekProvider(serial, logger), new MetaModeProvider(settings, logger), new SpdUnisocProvider(serial, logger), new DiagnosticProvider(serial, logger), new OperationRegistry());
        var discovery = new DeviceDiscoveryProvider(usb, adb, fb, new DeviceProfileRepository(), cap, logger);
        var dashVm = new DashboardViewModel(discovery, cap);

        // 1. Locked Fastboot: fastboot.read_vars & bootloader.verified_unlock available, flash.fastboot_partition & adb.* HIDDEN!
        await dashVm.RefreshDashboardCommand.ExecuteAsync();
        var lockedOpIds = dashVm.AvailableOperations.Select(o => o.Operation.OperationId).ToList();
        Assert.Contains("fastboot.read_vars", lockedOpIds);
        Assert.Contains("bootloader.verified_unlock", lockedOpIds);
        Assert.DoesNotContain("flash.fastboot_partition", lockedOpIds);
        Assert.DoesNotContain("adb.read_identity", lockedOpIds);

        // 2. Unlocked Fastboot: flash.fastboot_partition becomes available!
        unlocked = true;
        await dashVm.RefreshDashboardCommand.ExecuteAsync();
        var unlockedOpIds = dashVm.AvailableOperations.Select(o => o.Operation.OperationId).ToList();
        Assert.Contains("flash.fastboot_partition", unlockedOpIds);
    }

    [Fact]
    public async Task CrashReporter_WritesLocalRedactedCrashFile()
    {
        var ex = new InvalidOperationException("Failed auth with --password=MY_SUPER_SECRET_PASS");
        var crashDir = Path.Combine(_tempDir, "crashes");
        var path = CrashReportService.WriteCrashReport(ex, "SES-TEST", crashDir);

        Assert.True(File.Exists(path));
        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("MY_SUPER_SECRET_PASS", content);
        Assert.Contains("[REDACTED]", content);
    }
}
