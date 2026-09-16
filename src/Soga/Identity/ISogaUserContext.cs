namespace Soga.Identity;

/// <summary>Resolves the acting user from trusted host context.</summary>
public interface ISogaUserContext
{
    /// <summary>Returns the normalized authenticated user ID, or <see langword="null"/> when unavailable.</summary>
    ValueTask<string?> GetUserIdAsync(CancellationToken cancellationToken = default);
}
