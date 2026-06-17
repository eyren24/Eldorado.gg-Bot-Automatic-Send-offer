namespace EldoradoApp.Services;

/// <summary>Supplies a valid Eldorado IdToken (refreshing as needed) for the auth handler.</summary>
public interface IEldoradoTokenProvider
{
    bool IsSignedIn { get; }

    Task<string?> GetIdTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}
