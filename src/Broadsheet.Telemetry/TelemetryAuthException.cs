namespace Broadsheet.Telemetry;

/// <summary>Raised when the signed-in Azure identity is missing, expired, or lacks read access to the resource.</summary>
public sealed class TelemetryAuthException(string message, Exception? inner = null)
    : Exception(message, inner);
