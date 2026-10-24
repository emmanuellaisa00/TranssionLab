using TranssionLab.Core.Enums;

namespace TranssionLab.Core.Models;

public sealed record BootloaderAssessment(
    string DeviceKey,
    string? TransportSerial,
    ConnectionMode ActiveMode,
    BootloaderLockState ReportedLockState,
    bool? OemUnlockAllowedInOs,
    bool? FastbootGetUnlockAbility,
    DeviceProfile? MatchedProfile,
    bool IsUnlockWorkflowPermitted,
    bool Android16CaveatApplies,
    string StatusSummary,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<string> VerifiedInstructions,
    IReadOnlyList<string> DestructiveWarnings,
    string ExpectedConfirmationPhrase);

public sealed record BootloaderOperationResult(
    string OperationId,
    bool Executed,
    bool VerifiedStateMatched,
    BootloaderLockState ResultingState,
    int ExitCode,
    string Summary,
    string RawTranscript,
    DateTimeOffset CompletedAtUtc);

public sealed record FlashPartitionRequest(
    string PartitionName,
    string ImageFilePath,
    long ImageBytes,
    long EffectiveUnsparsedBytes,
    string ImageSha256);

public sealed record FlashProposedStep(
    int StepOrder,
    string ActionType, // precheck, flash, set_active, verify
    string TargetPartition,
    string CommandPreview,
    long PayloadBytes,
    bool ModifiesDevice,
    bool IsLogicalPartition,
    string Notes);

public sealed record FlashExecutionPlan(
    string PlanId,
    string DeviceKey,
    string TransportSerial,
    string MatchedProfileId,
    ConnectionMode ProtocolMode,
    IReadOnlyList<FlashPartitionRequest> Partitions,
    IReadOnlyList<FlashProposedStep> ProposedSteps,
    bool TouchesUserdata,
    bool BlockedBySafetyPolicy,
    IReadOnlyList<string> SafetyFindings,
    string PlanHashSha256,
    string RequiredConfirmationText,
    DateTimeOffset CreatedAtUtc);

public sealed record DryRunValidationReport(
    string PlanId,
    bool Passed,
    bool DeviceStillConnected,
    bool ProfileVerified,
    bool ProtocolReady,
    bool BootloaderUnlockedVerified,
    bool AllImagesReadableAndHashed,
    bool AllPartitionSizesCompatible,
    bool ProtectedPartitionsUntouched,
    IReadOnlyList<string> CheckResults,
    IReadOnlyList<string> BlockingErrors,
    DateTimeOffset ValidatedAtUtc);

public sealed record FlashConfirmationToken(
    string PlanId,
    string PlanHashSha256,
    string EnteredConfirmationText,
    bool UserAcknowledgedDataAndBrickRisk,
    DateTimeOffset ConfirmedAtUtc);

public sealed record FlashOperationReport(
    string OperationId,
    string PlanId,
    string SessionId,
    string DeviceKey,
    string TransportSerial,
    string ProfileId,
    ConnectionMode ProtocolUsed,
    bool DryRunOnly,
    bool Succeeded,
    bool PostStateVerified,
    string VerificationSummary,
    IReadOnlyList<string> StepTranscripts,
    IReadOnlyList<string> Errors,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? SavedReportPath);
