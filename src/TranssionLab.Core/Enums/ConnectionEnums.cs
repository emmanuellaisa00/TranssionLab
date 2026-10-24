namespace TranssionLab.Core.Enums;

/// <summary>
/// Explicitly distinguishes how a piece of device or firmware information was obtained.
/// </summary>
public enum DataProvenance
{
    Unknown = 0,
    Detected = 1,
    Inferred = 2,
    Verified = 3
}

/// <summary>
/// Canonical connection dashboard states required by Section 3.
/// </summary>
public enum ConnectionDashboardState
{
    DeviceDisconnected = 0,
    Connected = 1,
    Unauthorized = 2,
    WaitingForAuthorization = 3,
    DetectedButUnidentified = 4,
    ProtocolAvailable = 5,
    ProtocolUnsupported = 6,
    AuthenticationRequired = 7,
    OperationInProgress = 8,
    OperationFailed = 9,
    OperationVerified = 10
}

public enum ConnectionMode
{
    Unknown = 0,
    AdbAuthorized = 1,
    AdbUnauthorized = 2,
    AdbOffline = 3,
    Recovery = 4,
    AdbSideload = 5,
    FastbootBootloader = 6,
    FastbootdUserspace = 7,
    MediaTekBrom = 8,
    MediaTekPreloader = 9,
    MediaTekDownloadAgent = 10,
    MediaTekMeta = 11,
    UnisocDownload = 12,
    UnisocDiag = 13,
    QualcommDiag = 14,
    VendorDiag = 15,
    MtpOnly = 16,
    CompositeUsb = 17
}

public enum AdbDeviceState
{
    Unavailable = 0,
    Disconnected = 1,
    Unauthorized = 2,
    Authorizing = 3,
    Offline = 4,
    Connected = 5,
    Recovery = 6,
    Sideload = 7
}

public enum ProviderImplementationStatus
{
    Unavailable = 0,
    DetectionOnly = 1,
    ReadOnlyDiagnostics = 2,
    SupportedValidatedWorkflows = 3
}

public enum HardwareVerificationLevel
{
    SimulatedUnitTestedOnly = 0,
    ReferenceSpecification = 1,
    HardwareVerified = 2
}

public enum BootloaderLockState
{
    Unknown = 0,
    Locked = 1,
    Unlocked = 2,
    UnlockDisallowedByOemToggle = 3,
    PolicyRestricted = 4
}

public enum ChipsetVendor
{
    Unknown = 0,
    MediaTek = 1,
    Unisoc = 2,
    Qualcomm = 3,
    Other = 4
}

public enum AuthorizationRequirement
{
    None = 0,
    AdbRsaAuthorized = 1,
    BootloaderUnlocked = 2,
    ManufacturerSignedAuthOrDa = 3,
    LicensedMetaLibrary = 4,
    SignedUnisocFdl = 5,
    VerifiedDiagnosticInterface = 6
}

public enum FirmwareRequirement
{
    None = 0,
    SinglePartitionImage = 1,
    SignedOtaZip = 2,
    VerifiedMtkScatterPackage = 3,
    VerifiedUnisocPacPackage = 4,
    AnySupportedFirmwareArchive = 5
}

public enum DriverHealthStatus
{
    Healthy = 0,
    MissingDriverCode28 = 1,
    CannotStartCode10 = 2,
    DisabledCode22 = 3,
    DescriptorFailureCode43 = 4,
    UnsignedBlockedCode52 = 5,
    OtherProblem = 6,
    NotEvaluated = 7
}

public enum FirmwareFormatKind
{
    Unsupported = 0,
    ZipArchive = 1,
    AndroidOtaZip = 2,
    TarArchive = 3,
    TarGzipArchive = 4,
    AndroidPayloadBin = 5,
    MediaTekScatterText = 6,
    MediaTekScatterXml = 7,
    UnisocPacContainer = 8,
    AndroidSparseImage = 9,
    AndroidBootImage = 10,
    AndroidVendorBootImage = 11,
    AndroidVbmetaImage = 12,
    RawPartitionImage = 13,
    AndroidSuperImage = 14
}

public enum PartitionAccessPolicy
{
    SafeFlashable = 0,
    LogicalSuperRequiresFastbootd = 1,
    UserdataExplicitEraseOnly = 2,
    ProtectedNvramOrCalibrationBlocked = 3,
    BootChainCriticalBlocked = 4,
    AntiTheftFrpBlocked = 5
}

public enum LogSeverity
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
    Critical = 5
}
