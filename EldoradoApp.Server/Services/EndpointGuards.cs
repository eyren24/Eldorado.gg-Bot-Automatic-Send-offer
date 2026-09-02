using EldoradoApp.Server.Data;

namespace EldoradoApp.Server.Services;

internal static class EndpointGuards
{
    public static async Task<(DeviceSession? Session, IResult? Failure)> RequireDeviceAsync(
        HttpRequest request,
        DeviceAuthentication authentication,
        EldoradoDbContext db,
        CancellationToken cancellationToken)
    {
        var session = await authentication.AuthenticateAsync(request, cancellationToken);
        if (session is null)
        {
            return (null, Results.Unauthorized());
        }

        await db.SaveChangesAsync(cancellationToken); // persists LastSeenAtUtc
        return (session, null);
    }
}
