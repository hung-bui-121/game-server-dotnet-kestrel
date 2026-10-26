#nullable enable
using System;
using System.Collections.Generic;
using GameServer.Protocol.Core.V1;

namespace GameServer.Client
{
    /// <summary>Catalog names the SDK reacts to (API-11). Business codes are returned to the game unchanged.</summary>
    public static class ErrorCodes
    {
        public const string Ok = "OK";
        public const string BadRequest = "BAD_REQUEST";
        public const string Unauthenticated = "UNAUTHENTICATED";
        public const string RateLimited = "RATE_LIMITED";
        public const string RequestTimeout = "REQUEST_TIMEOUT";
        public const string Maintenance = "MAINTENANCE";
        public const string UpdateRequired = "UPDATE_REQUIRED";
        public const string ConcurrentUpdate = "CONCURRENT_UPDATE";
        public const string CommitUnknownRetry = "COMMIT_UNKNOWN_RETRY";
        public const string AccountSuspended = "ACCOUNT_SUSPENDED";
        public const string AccountDeleted = "ACCOUNT_DELETED";
        public const string SessionReplaced = "SESSION_REPLACED";
        public const string SwitchAccount = "SWITCH_ACCOUNT";

        /// <summary>Client-side code: no HTTP response after every retry.</summary>
        public const string NetworkError = "NETWORK_ERROR";

        /// <summary>Client-side code: the response could not be parsed.</summary>
        public const string InvalidResponse = "INVALID_RESPONSE";
    }

    /// <summary>An error answered by the server (<c>ErrorEnvelope</c>) or produced by the SDK.</summary>
    public sealed class GameServerError
    {
        public GameServerError(
            string code,
            int codeNumber,
            int httpStatus,
            string message,
            bool retryable,
            int retryAfterMs,
            string? requestId,
            IReadOnlyDictionary<string, string> details)
        {
            Code = code;
            CodeNumber = codeNumber;
            HttpStatus = httpStatus;
            Message = message;
            Retryable = retryable;
            RetryAfterMs = retryAfterMs;
            RequestId = requestId;
            Details = details;
        }

        public string Code { get; }

        public int CodeNumber { get; }

        public int HttpStatus { get; }

        public string Message { get; }

        public bool Retryable { get; }

        public int RetryAfterMs { get; }

        public string? RequestId { get; }

        /// <summary>Safe structured details, e.g. <c>until</c> for ACCOUNT_SUSPENDED.</summary>
        public IReadOnlyDictionary<string, string> Details { get; }

        public static GameServerError FromEnvelope(ErrorEnvelope envelope, int httpStatus) =>
            new GameServerError(
                envelope.Code,
                envelope.CodeNumber,
                httpStatus,
                envelope.Message,
                envelope.Retryable,
                envelope.RetryAfterMs,
                envelope.RequestId,
                new Dictionary<string, string>(envelope.Details));

        public static GameServerError Client(string code, string message, bool retryable = false) =>
            new GameServerError(code, -1, 0, message, retryable, 0, null, new Dictionary<string, string>());

        public override string ToString() => $"{Code} ({HttpStatus}): {Message}";
    }

    public sealed class GameServerException : Exception
    {
        public GameServerException(GameServerError error, Exception? inner = null)
            : base(error.ToString(), inner)
        {
            Error = error;
        }

        public GameServerError Error { get; }
    }
}
