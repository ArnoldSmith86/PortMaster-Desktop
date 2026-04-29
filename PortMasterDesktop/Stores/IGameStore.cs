using PortMasterDesktop.Models;

namespace PortMasterDesktop.Stores;

public interface IGameStore
{
    StoreId StoreId { get; }
    string DisplayName { get; }

    /// <summary>Returns true when stored credentials exist and are still valid.</summary>
    Task<bool> IsAuthenticatedAsync();

    /// <summary>
    /// Opens the OAuth browser flow (or shows a credential dialog for Steam).
    /// Returns true on success.
    /// </summary>
    Task<bool> AuthenticateAsync(CancellationToken ct = default);

    Task LogoutAsync();

    /// <summary>Username / display name for the currently authenticated account, or null.</summary>
    Task<string?> GetAccountNameAsync();

    /// <summary>Returns the user's full game library for this store.</summary>
    Task<IReadOnlyList<StoreGame>> GetLibraryAsync(CancellationToken ct = default);

    /// <summary>Clears any cached library data so the next GetLibraryAsync re-reads from source.</summary>
    Task InvalidateLibraryCacheAsync();

    /// <summary>
    /// Attempts to match a port store URL to a game the user owns.
    /// Returns null if not owned.
    /// </summary>
    Task<StoreGame?> FindOwnedGameAsync(string storeUrl, CancellationToken ct = default);

    /// <summary>Most recent error message from this store (HTTP failure, rate limit, etc.). Null when clean.</summary>
    string? LastError { get; }

    /// <summary>True while this store is being skipped after a recent error.</summary>
    bool IsInCooldown { get; }

    /// <summary>Stamp the store with an error message and put it in cooldown for the given duration.</summary>
    void RecordError(string message, TimeSpan? cooldown = null);

    /// <summary>Clear LastError and any active cooldown — call after a successful operation.</summary>
    void ClearError();
}
