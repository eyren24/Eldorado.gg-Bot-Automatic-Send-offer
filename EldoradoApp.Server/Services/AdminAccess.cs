using EldoradoApp.Server.Configuration;
using Microsoft.Extensions.Options;

namespace EldoradoApp.Server.Services;

public sealed class AdminAccess(IOptions<AdminOptions> options)
{
    public const string HeaderName = "X-Eldorado-Admin-Key";

    public bool IsAuthorized(HttpRequest request)
    {
        var expected = options.Value.ApiKey?.Trim() ?? "";
        var actual = request.Headers[HeaderName].FirstOrDefault()?.Trim() ?? "";
        return expected.Length > 0 && actual.Length > 0 && Hashing.FixedTimeEquals(expected, actual);
    }
}
