using System.Diagnostics;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;

namespace TranssionLab.Servicing;

/// <summary>
/// Bootloader Manager supporting Android 16 and earlier where verified (Section 5).
/// Never implements exploit-based unlocking or bypasses manufacturer authentication,
/// requires explicit destructive confirmation, and verifies actual post-command lock state.
/// </summary>
public sealed class BootloaderManager : IBootloaderManager
{
    private readonly IDeviceProfileRepository _profileRepo;
    private readonly IFastbootProvider _fastbootProvider;
    private readonly IAdbProvider _adbProvider;
    private readonly IStructuredLogger _logger;
    private readonly IOperationHistoryStore _historyStore;

    public BootloaderManager(
        IDeviceProfileRepository profileRepo,
        IFastbootProvider fastbootProvider,
        IAdbProvider adbProvider,
        IStructuredLogger logger,
        IOperationHistoryStore historyStore)
    {
        _profileRepo = profileRepo;
        _fastbootProvider = fastbootProvider;
        _adbProvider = adbProvider;
        _logger = logger;
        _historyStore = historyStore;
    }

    public async Task<BootloaderAssessment> AssessDeviceAsync(ConnectedDeviceSnapshot device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        var profile = !string.IsNullOrWhiteSpace(device.MatchedProfileId)
            ? _profileRepo.GetById(device.MatchedProfileId)
            : null;

        bool? oemAllowedInOs = null;
        bool? fbUnlockAbility = null;
        var lockState = device.BootloaderState.Value;

        if (device.ActiveConnectionMode == ConnectionMode.AdbAuthorized && !string.IsNullOrWhiteSpace(device.TransportSerial))
        {
            var props = device.RawProperties.Count > 0
                ? device.RawProperties
                : await _adbProvider.GetDevicePropertiesAsync(device.TransportSerial, ct).ConfigureAwait(false);

            if (props.TryGetValue("sys.oem_unlock_allowed", out var sysOem))
                oemAllowedInOs = sysOem == "1";
            else if (props.TryGetValue("ro.oem_unlock_supported", out var roOem) && roOem == "0")
                oemAllowedInOs = false;
        }
        else if (device.ActiveConnectionMode is ConnectionMode.FastbootBootloader or ConnectionMode.FastbootdUserspace &&
                 !string.IsNullOrWhiteSpace(device.TransportSerial))
        {
            fbUnlockAbility = await _fastbootProvider.QueryUnlockAbilityAsync(device.TransportSerial, ct).ConfigureAwait(false);
            var unlockedVar = await _fastbootProvider.GetVariableAsync(device.TransportSerial, "unlocked", ct).ConfigureAwait(false);
            if (string.Equals(unlockedVar, "yes", StringComparison.OrdinalIgnoreCase))
                lockState = BootloaderLockState.Unlocked;
            else if (string.Equals(unlockedVar, "no", StringComparison.OrdinalIgnoreCase))
                lockState = BootloaderLockState.Locked;
        }

        if (oemAllowedInOs == false || fbUnlockAbility == false)
        {
            lockState = lockState == BootloaderLockState.Unlocked
                ? BootloaderLockState.Unlocked
                : BootloaderLockState.UnlockDisallowedByOemToggle;
        }

        bool android16Caveat = (device.AndroidVersion.Value?.Contains("16", StringComparison.OrdinalIgnoreCase) ?? false) ||
                               (profile?.Android16NotesApplicable ?? false);

        var prereqs = new List<string>();
        var steps = new List<string>();
        var warnings = new List<string>
        {
            "Unlocking the bootloader triggers an immediate hardware-backed cryptographic wipe of /data (all user files, apps, and accounts are permanently erased).",
            "Android 16 (and earlier Android releases) do NOT inherently guarantee bootloader unlockability; manufacturer LK/U-Boot policy and regional/carrier restrictions govern.",
            "TranssionLab never implements BROM/FDL exploit unlocks or unauthorized token circumvention."
        };

        bool unlockPermitted = false;
        string summary;

        if (profile is null)
        {
            summary = "No verified TECNO/Infinix/itel Device Profile matched. Official unlock execution requires a verified profile so manufacturer-specific bootloader rules are respected.";
            prereqs.Add("Match or import a verified Device Profile for this exact model code / codename.");
        }
        else if (profile.BootloaderPolicy.RequiresManufacturerToken)
        {
            lockState = lockState == BootloaderLockState.Unlocked ? BootloaderLockState.Unlocked : BootloaderLockState.PolicyRestricted;
            summary = $"{profile.MarketingName} ({profile.ChipsetFamily}) enforces RSA manufacturer identifier token verification in bootloader. Unauthenticated fastboot unlock is blocked by OEM policy.";
            prereqs.AddRange(profile.BootloaderPolicy.Prerequisites);
            steps.AddRange(profile.BootloaderPolicy.DeviceSpecificSteps);
            warnings.AddRange(profile.BootloaderPolicy.DataLossAndSecurityWarnings);
        }
        else if (device.ActiveConnectionMode != ConnectionMode.FastbootBootloader)
        {
            summary = device.ActiveConnectionMode == ConnectionMode.FastbootdUserspace
                ? "Device is in Userspace Fastbootd (is-userspace=yes). Bootloader unlock commands are only accepted in primary Bootloader Fastboot mode."
                : $"Device is currently in {device.ActiveConnectionMode}. Reboot to Bootloader mode after enabling OEM Unlocking in Developer Options.";
            prereqs.AddRange(profile.BootloaderPolicy.Prerequisites);
            steps.AddRange(profile.BootloaderPolicy.DeviceSpecificSteps);
            warnings.AddRange(profile.BootloaderPolicy.DataLossAndSecurityWarnings);
        }
        else if (lockState == BootloaderLockState.Unlocked)
        {
            summary = $"Verified: {profile.MarketingName} bootloader is already UNLOCKED.";
            steps.AddRange(profile.BootloaderPolicy.DeviceSpecificSteps);
        }
        else if (fbUnlockAbility == false)
        {
            summary = "Bootloader reports get_unlock_ability = 0. Boot into Android OS, open Developer Options, and enable 'OEM unlocking' first.";
            prereqs.AddRange(profile.BootloaderPolicy.Prerequisites);
            steps.AddRange(profile.BootloaderPolicy.DeviceSpecificSteps);
            warnings.AddRange(profile.BootloaderPolicy.DataLossAndSecurityWarnings);
        }
        else
        {
            unlockPermitted = true;
            summary = $"Ready for official '{profile.BootloaderPolicy.UnlockCommand}' on {profile.MarketingName}. Explicit confirmation and physical volume-key approval on the handset are required.";
            prereqs.AddRange(profile.BootloaderPolicy.Prerequisites);
            steps.AddRange(profile.BootloaderPolicy.DeviceSpecificSteps);
            warnings.AddRange(profile.BootloaderPolicy.DataLossAndSecurityWarnings);
        }

        if (android16Caveat && profile?.Android16CompatibilityNote is not null)
        {
            warnings.Add($"Android 16 / GKI Note: {profile.Android16CompatibilityNote}");
        }

        var serialToken = device.TransportSerial ?? "UNKNOWN";
        var phrase = $"UNLOCK {serialToken} ERASE-DATA";

        _logger.LogInfo(
            "BootloaderManager",
            $"Assessed bootloader for {device.DeviceKey}: State={lockState}, Permitted={unlockPermitted}",
            summary,
            device.DeviceKey);

        return new BootloaderAssessment(
            device.DeviceKey,
            device.TransportSerial,
            device.ActiveConnectionMode,
            lockState,
            oemAllowedInOs,
            fbUnlockAbility,
            profile,
            unlockPermitted,
            android16Caveat,
            summary,
            prereqs,
            steps,
            warnings,
            phrase);
    }

    public async Task<BootloaderOperationResult> ExecuteVerifiedUnlockAsync(
        ConnectedDeviceSnapshot device,
        BootloaderAssessment assessment,
        string userConfirmationInput,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(assessment);

        var opId = $"OP-BL-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        var started = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        if (!assessment.IsUnlockWorkflowPermitted || assessment.MatchedProfile is null)
        {
            throw new SafetyPolicyViolationException($"Unlock execution is not permitted: {assessment.StatusSummary}");
        }

        if (device.ActiveConnectionMode != ConnectionMode.FastbootBootloader || string.IsNullOrWhiteSpace(device.TransportSerial))
        {
            throw new SafetyPolicyViolationException("Device must be connected in primary Bootloader Fastboot mode with a valid serial.");
        }

        if (!string.Equals(userConfirmationInput?.Trim(), assessment.ExpectedConfirmationPhrase, StringComparison.Ordinal))
        {
            throw new SafetyPolicyViolationException(
                $"Confirmation phrase mismatch. Type '{assessment.ExpectedConfirmationPhrase}' exactly to acknowledge mandatory data wipe.");
        }

        var serial = device.TransportSerial;
        var useLegacy = assessment.MatchedProfile.BootloaderPolicy.UsesLegacyOemUnlock;

        var cmdResult = await _fastbootProvider.ExecuteVerifiedUnlockCommandAsync(serial, useLegacy, ct).ConfigureAwait(false);

        // Post-operation state verification (Section 5: "Verifies the actual resulting bootloader state")
        var postUnlockedVar = await _fastbootProvider.GetVariableAsync(serial, "unlocked", ct).ConfigureAwait(false);
        var resultingState = postUnlockedVar?.ToLowerInvariant() switch
        {
            "yes" or "true" => BootloaderLockState.Unlocked,
            "no" or "false" => BootloaderLockState.Locked,
            _ => BootloaderLockState.Unknown
        };

        bool verifiedUnlocked = cmdResult.Succeeded && resultingState == BootloaderLockState.Unlocked;
        sw.Stop();
        var completed = DateTimeOffset.UtcNow;

        var summary = verifiedUnlocked
            ? $"Verified: Bootloader on {serial} reported 'unlocked: yes' after official unlock command."
            : $"Unlock NOT verified: Command exit={cmdResult.ExitCode}, post-check 'unlocked'='{postUnlockedVar ?? "unavailable"}'. Device remains {resultingState}.";

        await _historyStore.RecordAsync(new OperationHistoryRecord(
            opId,
            _logger.CurrentSessionId,
            started,
            completed,
            sw.ElapsedMilliseconds,
            "Bootloader",
            "Official Bootloader Unlock",
            device.DeviceKey,
            _fastbootProvider.ProviderName,
            verifiedUnlocked,
            false,
            summary,
            summary,
            null), ct).ConfigureAwait(false);

        return new BootloaderOperationResult(
            opId,
            Executed: true,
            VerifiedStateMatched: verifiedUnlocked,
            ResultingState: resultingState,
            ExitCode: cmdResult.ExitCode,
            Summary: summary,
            RawTranscript: $"{cmdResult.StandardOutput}\n{cmdResult.StandardError}".Trim(),
            CompletedAtUtc: completed);
    }
}
