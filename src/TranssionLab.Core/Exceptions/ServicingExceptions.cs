namespace TranssionLab.Core.Exceptions;

public class ServicingException : Exception
{
    public string ErrorCode { get; }

    public ServicingException(string errorCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }
}

public sealed class AmbiguousDeviceSelectionException : ServicingException
{
    public int ConnectedDeviceCount { get; }

    public AmbiguousDeviceSelectionException(int count)
        : base(
            "AMBIGUOUS_DEVICE_SELECTION",
            $"Multiple devices ({count}) are currently connected. Explicit device selection is required before executing any operation.")
    {
        ConnectedDeviceCount = count;
    }
}

public sealed class SafetyPolicyViolationException : ServicingException
{
    public SafetyPolicyViolationException(string message)
        : base("SAFETY_POLICY_VIOLATION", message)
    {
    }
}
