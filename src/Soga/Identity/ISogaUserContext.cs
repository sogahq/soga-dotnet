namespace Soga.Identity;

/// <summary>
/// Resolves the acting user from trusted host context.
/// </summary>
/// <remarks>
/// <para>
/// Soga never accepts a user ID from a request body. Implement this interface to map your own authentication to a
/// Soga user ID when the default claim-based resolver is not enough.
/// </para>
/// <para>
/// A custom implementation applies to HTTP requests only. SignalR connections always read the connection principal
/// using the configured claim type (ADR 0002).
/// </para>
/// </remarks>
public interface ISogaUserContext
{
    /// <summary>
    /// Gets the ID of the authenticated user making the current request.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the user ID, or
    /// <see langword="null"/> when no user is authenticated.
    /// </returns>
    ValueTask<string?> GetUserIdAsync(CancellationToken cancellationToken = default);
}
