using System.Security.Cryptography;

namespace EldoradoApp.Server.Services;

public sealed class DeviceTokenService
{
    public const string HeaderName = "X-Eldorado-Device-Token";

    public string CreateToken() => "eldo_dt_" + Hashing.Base64Url(RandomNumberGenerator.GetBytes(32));

    public string Digest(string token) => Hashing.Sha256Hex(token);
}
