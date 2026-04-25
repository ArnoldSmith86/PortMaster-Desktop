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

    /// <summary>
    /// Attempts to match a port store URL to a game the user owns.
    /// Returns null if not owned.
    /// </summary>
    Task<StoreGame?> FindOwnedGameAsync(string storeUrl, CancellationToken ct = default);
}
