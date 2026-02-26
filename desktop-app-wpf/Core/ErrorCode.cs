namespace PdfStampNgrokDesktop.Core;

public enum ErrorCode
{
    None = 0,
    InvalidInput = 1001,
    NotFound = 1002,
    Unauthorized = 1003,
    IoFailure = 2001,
    SerializationFailure = 2002,
    EncryptionFailure = 2003,
    BackendStartFailed = 3001,
    BackendHealthFailed = 3002,
    NgrokStartFailed = 3003,
    NgrokTunnelUnavailable = 3004,
    NgrokStoppedUnexpectedly = 3005,
    HealthCheckFailed = 3006,
    UpdateCheckFailed = 4001,
    Unknown = 9000,
}
