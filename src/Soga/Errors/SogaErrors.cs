namespace Soga.Errors;

/// <summary>Stable machine-readable Soga error codes. Clients must branch on these, never on English text.</summary>
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

internal sealed record SogaError(string Code, int StatusCode, string Title, string? Detail = null);

internal static class SogaErrors
{
    public static readonly SogaError Unauthorized = new(SogaErrorCodes.Unauthorized, 401, "Authentication required");
    public static readonly SogaError TenantUnavailable = new(SogaErrorCodes.TenantUnavailable, 403, "Tenant unavailable");
    public static readonly SogaError AuthorizationDenied = new(SogaErrorCodes.AuthorizationDenied, 403, "Operation not permitted");
    public static readonly SogaError ConversationNotFound = new(SogaErrorCodes.ConversationNotFound, 404, "Conversation not found");
    public static readonly SogaError ConversationNotGroup = new(SogaErrorCodes.ConversationNotGroup, 409, "Operation requires a group conversation");
    public static readonly SogaError ConversationArchived = new(SogaErrorCodes.ConversationArchived, 409, "Conversation is archived");
    public static readonly SogaError GroupsDisabled = new(SogaErrorCodes.GroupsDisabled, 403, "Group conversations are disabled");
    public static readonly SogaError GroupTooLarge = new(SogaErrorCodes.GroupTooLarge, 422, "Group is too large");
    public static readonly SogaError GroupLastOwner = new(SogaErrorCodes.GroupLastOwner, 409, "A group must keep at least one owner");
    public static readonly SogaError ParticipantNotFound = new(SogaErrorCodes.ParticipantNotFound, 404, "Participant not found");
    public static readonly SogaError MessageNotFound = new(SogaErrorCodes.MessageNotFound, 404, "Message not found");
    public static readonly SogaError MessageTooLong = new(SogaErrorCodes.MessageTooLong, 422, "Message is too long");
    public static readonly SogaError IdempotencyConflict = new(SogaErrorCodes.IdempotencyConflict, 409, "Client message ID already used");
    public static readonly SogaError MessageNotEditable = new(SogaErrorCodes.MessageNotEditable, 409, "Message cannot be edited");
    public static readonly SogaError MessageEditWindowExpired = new(SogaErrorCodes.MessageEditWindowExpired, 409, "Edit window has expired");
    public static readonly SogaError MessageNotDeletable = new(SogaErrorCodes.MessageNotDeletable, 409, "Message cannot be deleted");
    public static readonly SogaError ReplyInvalid = new(SogaErrorCodes.ReplyInvalid, 422, "Reply target is invalid");
    public static readonly SogaError CursorInvalid = new(SogaErrorCodes.CursorInvalid, 400, "Cursor is invalid");
    public static readonly SogaError RateLimited = new(SogaErrorCodes.RateLimited, 429, "Too many requests");
    public static readonly SogaError ProtocolMismatch = new(SogaErrorCodes.ProtocolMismatch, 400, "Protocol version is not supported");
    public static readonly SogaError InternalError = new(SogaErrorCodes.InternalError, 500, "Internal error");

    public static SogaError InvalidRequest(string detail) =>
        new(SogaErrorCodes.InvalidRequest, 400, "Invalid request", detail);

    public static SogaError MetadataInvalid(string detail) =>
        new(SogaErrorCodes.MetadataInvalid, 422, "Metadata is invalid", detail);

    /// <summary>The reason is a host-defined machine code, never message text.</summary>
    public static SogaError MessageRejected(string reasonCode) =>
        new(SogaErrorCodes.MessageRejected, 422, "Message was rejected", reasonCode);
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
