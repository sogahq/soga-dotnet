namespace Soga.Errors;

/// <summary>
/// Provides the stable, machine-readable error codes that Soga returns in the <c>code</c> field of every error
/// response.
/// </summary>
/// <remarks>
/// Codes are part of the public protocol and never change once released. Clients must branch on these codes, never on
/// the English <c>title</c> or <c>detail</c> text. Codes follow the pattern <c>soga.&lt;area&gt;.&lt;reason&gt;</c>.
/// </remarks>
public static class SogaErrorCodes
{
    /// <summary>The request has no authenticated user.</summary>
    public const string Unauthorized = "soga.auth.unauthorized";

    /// <summary>The trusted tenant could not be resolved.</summary>
    public const string TenantUnavailable = "soga.tenant.unavailable";

    /// <summary>The caller is a participant but their role or the host policy does not allow the operation.</summary>
    public const string AuthorizationDenied = "soga.authorization.denied";

    /// <summary>The conversation does not exist or is not visible to the caller.</summary>
    public const string ConversationNotFound = "soga.conversation.not_found";

    /// <summary>Reserved. Read and write paths return not_found so private conversations cannot be enumerated.</summary>
    public const string ConversationNotParticipant = "soga.conversation.not_participant";

    /// <summary>The operation needs a group conversation.</summary>
    public const string ConversationNotGroup = "soga.conversation.not_group";

    /// <summary>The conversation is archived and cannot receive new messages.</summary>
    public const string ConversationArchived = "soga.conversation.archived";

    /// <summary>Group conversations are disabled on this host.</summary>
    public const string GroupsDisabled = "soga.group.disabled";

    /// <summary>The group already has the maximum number of participants.</summary>
    public const string GroupTooLarge = "soga.group.too_large";

    /// <summary>The change would leave the group without an owner.</summary>
    public const string GroupLastOwner = "soga.group.last_owner";

    /// <summary>The target user is not a participant of the conversation.</summary>
    public const string ParticipantNotFound = "soga.participant.not_found";

    /// <summary>The message does not exist in the conversation.</summary>
    public const string MessageNotFound = "soga.message.not_found";

    /// <summary>The message text exceeds the configured limit.</summary>
    public const string MessageTooLong = "soga.message.too_long";

    /// <summary>The client message ID was already used for a different message.</summary>
    public const string IdempotencyConflict = "soga.message.idempotency_conflict";

    /// <summary>The message cannot be edited (system message, deleted message, or editing disabled).</summary>
    public const string MessageNotEditable = "soga.message.not_editable";

    /// <summary>The message is older than the configured edit window.</summary>
    public const string MessageEditWindowExpired = "soga.message.edit_window_expired";

    /// <summary>The message cannot be deleted (system message or deletion disabled).</summary>
    public const string MessageNotDeletable = "soga.message.not_deletable";

    /// <summary>A host message validator rejected the message.</summary>
    public const string MessageRejected = "soga.message.rejected";

    /// <summary>The reply target is missing, deleted, or in another conversation.</summary>
    public const string ReplyInvalid = "soga.reply.invalid";

    /// <summary>The conversation metadata is malformed or exceeds a limit.</summary>
    public const string MetadataInvalid = "soga.metadata.invalid";

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
/// Represents a failure that is returned to the client as a Problem Details response.
/// </summary>
/// <param name="Code">The stable error code from <see cref="SogaErrorCodes"/>.</param>
/// <param name="StatusCode">The HTTP status code of the response.</param>
/// <param name="Title">A short, fixed English summary. Clients must not parse it.</param>
/// <param name="Detail">
/// An optional explanation that is safe to show to the caller. It never contains message text, tokens, or details
/// about other users.
/// </param>
internal sealed record SogaError(string Code, int StatusCode, string Title, string? Detail = null);

/// <summary>
/// Provides the catalogue of <see cref="SogaError"/> values that workflows return.
/// </summary>
/// <remarks>
/// Keeping every error in one place guarantees that the same failure always produces the same code, status, and
/// title, whether it is reached over HTTP or SignalR.
/// </remarks>
internal static class SogaErrors
{
    /// <summary>Gets the error for a request without an authenticated user (HTTP 401).</summary>
    public static readonly SogaError Unauthorized = new(SogaErrorCodes.Unauthorized, 401, "Authentication required");

    /// <summary>Gets the error for a request whose tenant cannot be resolved (HTTP 403).</summary>
    public static readonly SogaError TenantUnavailable = new(SogaErrorCodes.TenantUnavailable, 403, "Tenant unavailable");

    /// <summary>Gets the error for a participant whose role or host policy forbids the operation (HTTP 403).</summary>
    public static readonly SogaError AuthorizationDenied = new(SogaErrorCodes.AuthorizationDenied, 403, "Operation not permitted");

    /// <summary>
    /// Gets the error for a conversation that does not exist, is in another tenant, or does not include the caller
    /// (HTTP 404). All three cases look identical so outsiders cannot discover private conversations.
    /// </summary>
    public static readonly SogaError ConversationNotFound = new(SogaErrorCodes.ConversationNotFound, 404, "Conversation not found");

    /// <summary>Gets the error for a group-only operation attempted on a direct conversation (HTTP 409).</summary>
    public static readonly SogaError ConversationNotGroup = new(SogaErrorCodes.ConversationNotGroup, 409, "Operation requires a group conversation");

    /// <summary>Gets the error for a write to an archived conversation (HTTP 409).</summary>
    public static readonly SogaError ConversationArchived = new(SogaErrorCodes.ConversationArchived, 409, "Conversation is archived");

    /// <summary>Gets the error for a group operation when groups are disabled by the host (HTTP 403).</summary>
    public static readonly SogaError GroupsDisabled = new(SogaErrorCodes.GroupsDisabled, 403, "Group conversations are disabled");

    /// <summary>Gets the error for adding participants beyond the configured group size (HTTP 422).</summary>
    public static readonly SogaError GroupTooLarge = new(SogaErrorCodes.GroupTooLarge, 422, "Group is too large");

    /// <summary>Gets the error for a change that would leave a group without an owner (HTTP 409).</summary>
    public static readonly SogaError GroupLastOwner = new(SogaErrorCodes.GroupLastOwner, 409, "A group must keep at least one owner");

    /// <summary>Gets the error for a target user who is not a participant of the conversation (HTTP 404).</summary>
    public static readonly SogaError ParticipantNotFound = new(SogaErrorCodes.ParticipantNotFound, 404, "Participant not found");

    /// <summary>Gets the error for a message that does not exist in the conversation (HTTP 404).</summary>
    public static readonly SogaError MessageNotFound = new(SogaErrorCodes.MessageNotFound, 404, "Message not found");

    /// <summary>Gets the error for message text longer than the configured limit (HTTP 422).</summary>
    public static readonly SogaError MessageTooLong = new(SogaErrorCodes.MessageTooLong, 422, "Message is too long");

    /// <summary>
    /// Gets the error for a client message ID that was already used with different text (HTTP 409). The stored
    /// message is never returned with this error (ADR 0011).
    /// </summary>
    public static readonly SogaError IdempotencyConflict = new(SogaErrorCodes.IdempotencyConflict, 409, "Client message ID already used");

    /// <summary>Gets the error for editing a system message, a deleted message, or editing while disabled (HTTP 409).</summary>
    public static readonly SogaError MessageNotEditable = new(SogaErrorCodes.MessageNotEditable, 409, "Message cannot be edited");

    /// <summary>Gets the error for editing a message after the configured edit window (HTTP 409).</summary>
    public static readonly SogaError MessageEditWindowExpired = new(SogaErrorCodes.MessageEditWindowExpired, 409, "Edit window has expired");

    /// <summary>Gets the error for deleting a system message or deleting while disabled (HTTP 409).</summary>
    public static readonly SogaError MessageNotDeletable = new(SogaErrorCodes.MessageNotDeletable, 409, "Message cannot be deleted");

    /// <summary>Gets the error for a reply whose target is missing, deleted, or in another conversation (HTTP 422).</summary>
    public static readonly SogaError ReplyInvalid = new(SogaErrorCodes.ReplyInvalid, 422, "Reply target is invalid");

    /// <summary>Gets the error for a malformed, tampered, or out-of-scope cursor (HTTP 400).</summary>
    public static readonly SogaError CursorInvalid = new(SogaErrorCodes.CursorInvalid, 400, "Cursor is invalid");

    /// <summary>Gets the error for a caller who exceeded a rate limit (HTTP 429).</summary>
    public static readonly SogaError RateLimited = new(SogaErrorCodes.RateLimited, 429, "Too many requests");

    /// <summary>Gets the error for a client that asks for an unsupported protocol version (HTTP 400).</summary>
    public static readonly SogaError ProtocolMismatch = new(SogaErrorCodes.ProtocolMismatch, 400, "Protocol version is not supported");

    /// <summary>Gets the error for an unexpected failure (HTTP 500). It never includes exception details.</summary>
    public static readonly SogaError InternalError = new(SogaErrorCodes.InternalError, 500, "Internal error");

    /// <summary>
    /// Creates the error for a malformed request or a request that fails validation (HTTP 400).
    /// </summary>
    /// <param name="detail">A safe explanation of what is wrong with the request.</param>
    /// <returns>A <see cref="SogaError"/> with the <see cref="SogaErrorCodes.InvalidRequest"/> code.</returns>
    public static SogaError InvalidRequest(string detail) =>
        new(SogaErrorCodes.InvalidRequest, 400, "Invalid request", detail);

    /// <summary>
    /// Creates the error for conversation metadata that is malformed or exceeds a limit (HTTP 422).
    /// </summary>
    /// <param name="detail">A safe explanation of the metadata rule that was broken.</param>
    /// <returns>A <see cref="SogaError"/> with the <see cref="SogaErrorCodes.MetadataInvalid"/> code.</returns>
    public static SogaError MetadataInvalid(string detail) =>
        new(SogaErrorCodes.MetadataInvalid, 422, "Metadata is invalid", detail);

    /// <summary>
    /// Creates the error for a message rejected by a host message validator (HTTP 422).
    /// </summary>
    /// <param name="reasonCode">
    /// The machine-readable reason supplied by the host validator. It is a code, never message text.
    /// </param>
    /// <returns>A <see cref="SogaError"/> with the <see cref="SogaErrorCodes.MessageRejected"/> code.</returns>
    public static SogaError MessageRejected(string reasonCode) =>
        new(SogaErrorCodes.MessageRejected, 422, "Message was rejected", reasonCode);
}

/// <summary>
/// Represents the outcome of a workflow operation: either a value or a <see cref="SogaError"/>.
/// </summary>
/// <typeparam name="T">The type of the value returned on success.</typeparam>
/// <remarks>
/// Workflows return expected failures, such as "not found" or "too long", as results instead of throwing, so the HTTP
/// and SignalR layers can map them to stable responses. Exceptions are reserved for unexpected faults.
/// </remarks>
internal sealed class SogaResult<T>
{
    /// <summary>
    /// The value of a successful result.
    /// </summary>
    private readonly T? _value;

    /// <summary>
    /// Initializes a new instance of the <see cref="SogaResult{T}"/> class.
    /// </summary>
    /// <param name="value">The value, when the result is successful.</param>
    /// <param name="error">The error, when the result is a failure.</param>
    private SogaResult(T? value, SogaError? error)
    {
        _value = value;
        Error = error;
    }

    /// <summary>
    /// Gets the error of a failed result, or <see langword="null"/> when the result is successful.
    /// </summary>
    public SogaError? Error { get; }

    /// <summary>
    /// Gets a value indicating whether the operation succeeded.
    /// </summary>
    public bool IsSuccess => Error is null;

    /// <summary>
    /// Gets the value of a successful result.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is a failure and has no value.</exception>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <param name="value">The value produced by the operation.</param>
    /// <returns>A successful <see cref="SogaResult{T}"/> that contains <paramref name="value"/>.</returns>
    public static SogaResult<T> Ok(T value) => new(value, null);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <param name="error">The error that describes the failure.</param>
    /// <returns>A failed <see cref="SogaResult{T}"/> that contains <paramref name="error"/>.</returns>
    public static SogaResult<T> Fail(SogaError error) => new(default, error);
}

/// <summary>
/// Represents the value of a create-or-get operation and whether it created something new.
/// </summary>
/// <typeparam name="T">The type of the created or existing value.</typeparam>
/// <param name="Value">The created or existing value.</param>
/// <param name="Created">
/// <see langword="true"/> if the operation created the value (HTTP 201); <see langword="false"/> if it already existed,
/// for example on an idempotent retry (HTTP 200).
/// </param>
internal sealed record CreatedResult<T>(T Value, bool Created);
