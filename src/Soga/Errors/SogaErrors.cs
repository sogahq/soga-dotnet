namespace Soga.Errors;

/// <summary>Stable machine-readable Soga error codes. Clients must branch on these, never on English text.</summary>
public static class SogaErrorCodes
{
    /// <summary>The request has no authenticated user.</summary>
    public const string Unauthorized = "soga.auth.unauthorized";

    /// <summary>The trusted tenant could not be resolved.</summary>
    public const string TenantUnavailable = "soga.tenant.unavailable";

    /// <summary>The conversation does not exist or is not visible to the caller.</summary>
    public const string ConversationNotFound = "soga.conversation.not_found";

    /// <summary>Reserved. Read and write paths return not_found so private conversations cannot be enumerated.</summary>
    public const string ConversationNotParticipant = "soga.conversation.not_participant";

    /// <summary>The message does not exist in the conversation.</summary>
    public const string MessageNotFound = "soga.message.not_found";

    /// <summary>The message text exceeds the configured limit.</summary>
    public const string MessageTooLong = "soga.message.too_long";

    /// <summary>The client message ID was already used for a different message.</summary>
    public const string IdempotencyConflict = "soga.message.idempotency_conflict";

    /// <summary>The cursor is malformed, tampered with, or belongs to another scope.</summary>
    public const string CursorInvalid = "soga.cursor.invalid";

    /// <summary>The caller exceeded a rate limit.</summary>
    public const string RateLimited = "soga.rate_limited";

    /// <summary>The client protocol version is not supported.</summary>
    public const string ProtocolMismatch = "soga.protocol.mismatch";

    /// <summary>The request is malformed or fails validation.</summary>
    public const string InvalidRequest = "soga.request.invalid";

    /// <summary>An unexpected server error occurred.</summary>
    public const string InternalError = "soga.internal_error";
}

internal sealed record SogaError(string Code, int StatusCode, string Title, string? Detail = null);

internal static class SogaErrors
{
    public static readonly SogaError Unauthorized = new(SogaErrorCodes.Unauthorized, 401, "Authentication required");
    public static readonly SogaError TenantUnavailable = new(SogaErrorCodes.TenantUnavailable, 403, "Tenant unavailable");
    public static readonly SogaError ConversationNotFound = new(SogaErrorCodes.ConversationNotFound, 404, "Conversation not found");
    public static readonly SogaError MessageNotFound = new(SogaErrorCodes.MessageNotFound, 404, "Message not found");
    public static readonly SogaError MessageTooLong = new(SogaErrorCodes.MessageTooLong, 422, "Message is too long");
    public static readonly SogaError IdempotencyConflict = new(SogaErrorCodes.IdempotencyConflict, 409, "Client message ID already used");
    public static readonly SogaError CursorInvalid = new(SogaErrorCodes.CursorInvalid, 400, "Cursor is invalid");
    public static readonly SogaError RateLimited = new(SogaErrorCodes.RateLimited, 429, "Too many requests");
    public static readonly SogaError ProtocolMismatch = new(SogaErrorCodes.ProtocolMismatch, 400, "Protocol version is not supported");
    public static readonly SogaError InternalError = new(SogaErrorCodes.InternalError, 500, "Internal error");

    public static SogaError InvalidRequest(string detail) =>
        new(SogaErrorCodes.InvalidRequest, 400, "Invalid request", detail);
}

internal sealed class SogaResult<T>
{
    private readonly T? _value;

    private SogaResult(T? value, SogaError? error)
    {
        _value = value;
        Error = error;
    }

    public SogaError? Error { get; }

    public bool IsSuccess => Error is null;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    public static SogaResult<T> Ok(T value) => new(value, null);

    public static SogaResult<T> Fail(SogaError error) => new(default, error);
}

internal sealed record CreatedResult<T>(T Value, bool Created);
