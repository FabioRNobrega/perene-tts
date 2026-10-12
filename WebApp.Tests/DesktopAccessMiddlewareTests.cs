using Microsoft.AspNetCore.Http;
using PereneTts.Desktop;

namespace WebApp.Tests;

public sealed class DesktopAccessMiddlewareTests
{
    private static readonly DesktopAccess Access = new("launch-token-value", "cookie-value");

    private static async Task<(HttpContext Context, bool NextCalled)> Send(string path, string query = "", string? cookie = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        if (cookie is not null) context.Request.Headers.Cookie = $"{DesktopAccessMiddleware.CookieName}={cookie}";
        var nextCalled = false;
        await new DesktopAccessMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, Access).InvokeAsync(context);
        return (context, nextCalled);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/_blazor")]
    [InlineData("/audio/6f1c2a52-9a1e-4c55-8a5e-1f2b3c4d5e6f")]
    public async Task RequestsWithoutTheCookieAreForbidden(string path)
    {
        var (context, nextCalled) = await Send(path);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task TheLaunchUrlIssuesAStrictHttpOnlyCookieAndRedirects()
    {
        var (context, nextCalled) = await Send("/", "?launch=launch-token-value");

        Assert.Equal(302, context.Response.StatusCode);
        Assert.Equal("/", context.Response.Headers.Location.ToString());
        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith($"{DesktopAccessMiddleware.CookieName}=cookie-value", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.False(nextCalled);
    }

    [Theory]
    [InlineData("?launch=wrong")]
    [InlineData("?launch=")]
    [InlineData("?launch=launch-token-valuE")]
    public async Task AWrongLaunchValueIsForbidden(string query)
    {
        var (context, nextCalled) = await Send("/", query);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task AValidCookiePasses()
    {
        var (context, nextCalled) = await Send("/_blazor", cookie: "cookie-value");
        Assert.True(nextCalled);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task TheLaunchTokenIsNotAValidCookie()
    {
        var (_, nextCalled) = await Send("/", cookie: "launch-token-value");
        Assert.False(nextCalled);
    }

    [Fact]
    public void SecretsAreRandomAndUrlSafe()
    {
        var first = DesktopAccess.Create();
        var second = DesktopAccess.Create();
        Assert.NotEqual(first.LaunchToken, first.CookieValue);
        Assert.NotEqual(first.LaunchToken, second.LaunchToken);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.LaunchToken);
    }
}
