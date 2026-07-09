using DropHarvester.Models.Auth;

namespace DropHarvester.Services;

/// <summary>Persists <see cref="AuthState"/> (token + device id) as JSON in the app data folder.</summary>
public interface IAuthStore
{
    /// <summary>Load the saved auth state, or a fresh empty state when none exists.</summary>
    /// <returns>The persisted or default auth state.</returns>
    AuthState Load();
    /// <summary>Persist the given auth state.</summary>
    /// <param name="state">The auth state to save.</param>
    void Save(AuthState state);
    /// <summary>Delete any persisted auth state.</summary>
    void Clear();
}

/// <summary>Default <see cref="IAuthStore"/> backed by <see cref="JsonStore"/> and an auth.json file.</summary>
public sealed class AuthStore : IAuthStore
{
    const string FileName = "auth.json";

    /// <summary>Load the saved auth state, or a fresh empty state when none exists.</summary>
    /// <returns>The persisted or default auth state.</returns>
    public AuthState Load() => JsonStore.Load<AuthState>(FileName);
    /// <summary>Persist the given auth state.</summary>
    /// <param name="state">The auth state to save.</param>
    public void Save(AuthState state) => JsonStore.Save(FileName, state);
    /// <summary>Delete any persisted auth state.</summary>
    public void Clear() => JsonStore.Delete(FileName);
}
