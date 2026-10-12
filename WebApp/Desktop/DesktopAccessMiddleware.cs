using System.Security.Cryptography;
using System.Text;
namespace PereneTts.Desktop;

/// <summary>Desktop mode: only the Perene TTS window may drive the studio. The window first loads "/?launch=&lt;token&gt;",
/// which issues a per-launch session cookie; every other request without that cookie gets 403.</summary>
public sealed class DesktopAccessMiddleware(RequestDelegate next, DesktopAccess access)
{
    public const string CookieName = "perene-launch";

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Path == "/" && request.Query.TryGetValue("launch", out var launch))
        {
            if (!Matches(launch.ToString(), access.LaunchToken)) return Forbid(context);
            context.Response.Cookies.Append(CookieName, access.CookieValue, new CookieOptions
            { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true });
            context.Response.Redirect("/");
            return Task.CompletedTask;
        }
        return request.Cookies.TryGetValue(CookieName, out var cookie) && Matches(cookie, access.CookieValue)
            ? next(context) : Forbid(context);
    }

    private static Task Forbid(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static bool Matches(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
}

/// <summary>Per-launch secrets: the URL token the window loads once, and the separate cookie value it receives.</summary>
public sealed record DesktopAccess(string LaunchToken, string CookieValue)
{
    public static DesktopAccess Create() => new(NewSecret(), NewSecret());
    public static string NewSecret() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
