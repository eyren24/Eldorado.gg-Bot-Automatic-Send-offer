namespace EldoradoApp.Services;

/// <summary>
/// The currently active seller authentication. Delegates token retrieval to whichever
/// provider the user signed in with (SRP email/password or Google OAuth), so the
/// <see cref="EldoradoAuthHandler"/> doesn't need to know which method is in use.
/// </summary>
public sealed class SellerSession : IEldoradoTokenProvider
{
    private IEldoradoTokenProvider? _active;

    /// <summary>How the active session was established (for UI display).</summary>
    public SellerAuthMethod Method { get; private set; } = SellerAuthMethod.None;

    public bool IsSignedIn => _active?.IsSignedIn ?? false;

    public void Use(IEldoradoTokenProvider provider, SellerAuthMethod method)
    {
        _active = provider;
        Method = method;
    }

    public void Clear()
    {
        _active = null;
        Method = SellerAuthMethod.None;
    }

    public Task<string?> GetIdTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        => _active?.GetIdTokenAsync(forceRefresh, cancellationToken) ?? Task.FromResult<string?>(null);
}

public enum SellerAuthMethod
{
    None,
    EmailPassword,
    Google,
    ManualToken,
}
