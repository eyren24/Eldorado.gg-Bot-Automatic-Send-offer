namespace EldoradoApp.Models;

/// <summary>
/// Seller login used for Cognito SRP authentication. Persisted encrypted at rest
/// via <see cref="Services.CredentialStore"/> (Windows DPAPI, per-user).
/// </summary>
public sealed record EldoradoCredentials(string Email, string Password);
