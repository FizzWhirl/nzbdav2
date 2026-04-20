using System.Net;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
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
        // These requests must include both the analysis mode marker and the internal auth token.
        app.Use(async (context, next) =>
        {
            var requestToken = context.Request.Headers["X-Internal-Analysis-Auth"].FirstOrDefault();
            var expectedToken = EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY");
            var hasAnalysisMode = context.Request.Headers.ContainsKey("X-Analysis-Mode");
            var isLoopback = context.Connection.RemoteIpAddress != null
                             && IPAddress.IsLoopback(context.Connection.RemoteIpAddress);

            if (hasAnalysisMode && isLoopback && IsTokenMatch(expectedToken, requestToken))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "internal-analysis")],
                    "InternalAnalysis"));
            }
            await next();
        });

        app.UseAuthentication();
    }

    private static bool IsTokenMatch(string expectedToken, string? requestToken)
    {
        if (string.IsNullOrWhiteSpace(requestToken))
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        var actualBytes = Encoding.UTF8.GetBytes(requestToken);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}