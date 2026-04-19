using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Auth;

public static class WebApplicationAuthExtensions
{
    private const string DisableWebdavAuthEnvVar = "DISABLE_WEBDAV_AUTH";

    private const string DisabledWebdavAuthLog =
        "WebDAV authentication is DISABLED via the DISABLE_WEBDAV_AUTH environment variable";

    private static readonly Action<Action> LogOnlyOnce = DebounceUtil.RunOnlyOnce();

    public static bool IsWebdavAuthDisabled()
    {
        var isWebdavAuthDisabled = EnvironmentUtil.IsVariableTrue(DisableWebdavAuthEnvVar);
        if (isWebdavAuthDisabled) LogOnlyOnce(() => Log.Information(DisabledWebdavAuthLog));
        return isWebdavAuthDisabled;
    }

    public static void UseWebdavBasicAuthentication(this WebApplication app)
    {
        if (IsWebdavAuthDisabled()) return;

        // Bypass Basic auth for internal analysis requests (ffprobe/ffmpeg) from localhost.
        // These requests send X-Analysis-Mode: true but cannot provide Basic credentials.
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Analysis-Mode") &&
                context.Connection.RemoteIpAddress != null &&
                IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "internal-analysis")],
                    "InternalAnalysis"));
            }
            await next();
        });

        app.UseAuthentication();
    }
}