namespace Soga.Errors;

/// <summary>Stable machine-readable Soga error codes. Clients must branch on these, never on English text.</summary>
/// <remarks>
/// Codes are part of the public contract: once released, a code's value must not change.
/// Codes are namespaced as <c>soga.&lt;area&gt;.&lt;reason&gt;</c>.
/// </remarks>
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

/// <summary>
/// An error to return to the client.
/// </summary>
/// <param name="Code">The stable machine-readable code from <see cref="SogaErrorCodes"/>.</param>
/// <param name="StatusCode">The HTTP status code to respond with.</param>
/// <param name="Title">A short human-readable summary. For display only; clients must not branch on it.</param>
/// <param name="Details">Optional extra information about this specific occurrence.</param>
internal sealed record SogaError(string Code, int StatusCode, string Title, string? Details = null);

/// <summary>
/// Central place that maps each <see cref="SogaErrorCodes"/> value to its HTTP status and title,
/// so the same error is always reported the same way.
/// </summary>
internal static class SogaErrorFactory
{
    // Auth and tenant errors.
    public static readonly SogaError Unauthorized = new(SogaErrorCodes.Unauthorized, 401, "Authentication required");
    public static readonly SogaError TenantUnavailable = new(SogaErrorCodes.TenantUnavailable, 403, "Tenant unavailable");

    // Missing resources. Also returned when the caller is not allowed to see the resource (404, not 403),
    // so the existence of private conversations is never revealed.
    public static readonly SogaError ConversationNotFound = new(SogaErrorCodes.ConversationNotFound, 404, "Conversation not found");
    public static readonly SogaError MessageNotFound = new(SogaErrorCodes.MessageNotFound, 404, "Message not found");

    // Message rules.
    public static readonly SogaError MessageTooLong = new(SogaErrorCodes.MessageTooLong, 422, "Message is too long");
    public static readonly SogaError IdempotencyConflict = new(SogaErrorCodes.IdempotencyConflict, 409, "Client message ID already used");

    // Request-level errors.
    public static readonly SogaError CursorInvalid = new(SogaErrorCodes.CursorInvalid, 400, "Cursor is invalid");
    public static readonly SogaError RateLimited = new(SogaErrorCodes.RateLimited, 429, "Too many requests");
    public static readonly SogaError ProtocolMismatch = new(SogaErrorCodes.ProtocolMismatch, 400, "Protocol version is not supported");
    public static readonly SogaError InternalError = new(SogaErrorCodes.InternalError, 500, "Internal error");

    /// <summary>
    /// Creates an invalid-request error. This is a method rather than a shared instance
    /// because each occurrence carries its own <paramref name="detail"/> message.
    /// </summary>
    /// <param name="detail">What is wrong with the request.</param>
    public static SogaError InvalidRequest(string detail) =>
        new(SogaErrorCodes.InvalidRequest, 400, "Invalid request", detail);
}

/// <summary>
/// The outcome of an operation: either a value or a <see cref="SogaError"/>, never both.
/// Lets callers handle expected failures without using exceptions.
/// </summary>
/// <typeparam name="T">The type of the value returned on success.</typeparam>
internal sealed class SogaResult<T>
{
    private readonly T? _value;

    private SogaResult(T? value, SogaError? error)
    {
        _value = value;
        Error = error;
    }

    /// <summary>The error when the operation failed; <see langword="null"/> on success.</summary>
    public SogaError? Error { get; }

    public bool IsSuccess => Error is null;



    public bool IsFailure => Error is not null;

    /// <summary>The success value.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the result is a failure. Check <see cref="IsSuccess"/> first.</exception>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    /// <summary>Creates a successful result holding <paramref name="value"/>.</summary>
    public static SogaResult<T> Ok(T value) => new(value, null);

    /// <summary>Creates a failed result holding <paramref name="error"/>.</summary>
    public static SogaResult<T> Fail(SogaError error) => new(default, error);
}

/// <summary>A value together with whether it was newly created (<see langword="true"/>) or already existed.</summary>
/// <remarks>
/// Used for idempotent operations, e.g. to let the API return 201 for a new message and 200 for a retry.
/// </remarks>
internal sealed record CreatedResult<T>(T Value, bool Created);
