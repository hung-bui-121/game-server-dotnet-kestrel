namespace GameServer.Errors;

/// <summary>Framework error codes, range 0–999 (API-05, API-11).</summary>
public static class CoreErrorCodes
{
    public const int RangeStart = 0;
    public const int RangeEnd = 999;

    public static readonly ErrorCode Ok = new(0, "OK", 200, false, "OK");
    public static readonly ErrorCode BadRequest = new(1, "BAD_REQUEST", 400, false, "The request is invalid.");
    public static readonly ErrorCode Unauthenticated = new(2, "UNAUTHENTICATED", 401, false, "Authentication is required.");
    public static readonly ErrorCode Forbidden = new(3, "FORBIDDEN", 403, false, "The caller is not allowed to perform this action.");
    public static readonly ErrorCode NotFound = new(4, "NOT_FOUND", 404, false, "The resource was not found.");
    public static readonly ErrorCode PayloadTooLarge = new(5, "PAYLOAD_TOO_LARGE", 413, false, "The request body is too large.");
    public static readonly ErrorCode UnsupportedMediaType = new(6, "UNSUPPORTED_MEDIA_TYPE", 415, false, "The content type is not supported.");
    public static readonly ErrorCode RateLimited = new(7, "RATE_LIMITED", 429, true, "Too many requests. Retry later.");
    public static readonly ErrorCode RequestTimeout = new(8, "REQUEST_TIMEOUT", 503, true, "The request timed out. Retry later.");
    public static readonly ErrorCode Maintenance = new(9, "MAINTENANCE", 503, true, "The service is under maintenance.");
    public static readonly ErrorCode UpdateRequired = new(10, "UPDATE_REQUIRED", 426, false, "A newer client version is required.");
    public static readonly ErrorCode IdempotencyPayloadMismatch = new(11, "IDEMPOTENCY_PAYLOAD_MISMATCH", 409, false, "The requestId was already used with a different payload.");
    public static readonly ErrorCode ConcurrentUpdate = new(12, "CONCURRENT_UPDATE", 409, true, "The data was changed concurrently. Retry with the same requestId.");
    public static readonly ErrorCode CommitUnknownRetry = new(13, "COMMIT_UNKNOWN_RETRY", 503, true, "The outcome is unknown. Retry with the same requestId.");
    public static readonly ErrorCode AggregateTooLarge = new(14, "AGGREGATE_TOO_LARGE", 200, false, "The change would exceed the data size limit.");
    public static readonly ErrorCode Internal = new(15, "INTERNAL", 500, false, "An internal error occurred.");

    public static ErrorCodeSet All { get; } = new(
        RangeStart,
        RangeEnd,
        Ok,
        BadRequest,
        Unauthenticated,
        Forbidden,
        NotFound,
        PayloadTooLarge,
        UnsupportedMediaType,
        RateLimited,
        RequestTimeout,
        Maintenance,
        UpdateRequired,
        IdempotencyPayloadMismatch,
        ConcurrentUpdate,
        CommitUnknownRetry,
        AggregateTooLarge,
        Internal);
}
