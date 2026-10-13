using PereneTts.Desktop;

namespace WebApp.Tests;

public sealed class DesktopLaunchTests
{
    [Fact]
    public void DesktopArgumentSelectsDesktopMode() =>
        Assert.True(DesktopLaunch.ShouldRun(["--desktop"], "/workspace/WebApp/bin/Debug/net10.0/"));

    [Theory]
    [InlineData("/Applications/Perene TTS.app/Contents/Resources/dotnet/")]
    [InlineData("/Applications/Perene TTS.app/Contents/Resources/dotnet")]
    [InlineData("/private/var/folders/x/AppTranslocation/1234/d/Perene TTS.app/Contents/Resources/dotnet/")]
    public void AppBundlePathSelectsDesktopModeWithoutArguments(string baseDirectory) =>
        Assert.True(DesktopLaunch.ShouldRun([], baseDirectory));

    [Theory]
    [InlineData("/workspace/WebApp/bin/Debug/net10.0/")]
    [InlineData("/app/")]
    [InlineData("/Users/ana/dotnet/")]
    [InlineData("/Users/ana/Contents/Resources/dotnet/")]
    [InlineData("/Users/ana/.app/Contents/Resources/dotnet/")]
    [InlineData("/Applications/Perene TTS.app/Contents/MacOS/")]
    [InlineData("/Applications/Perene TTS.app/Contents/dotnet/")]
    public void OtherDirectoriesKeepWebMode(string baseDirectory) =>
        Assert.False(DesktopLaunch.ShouldRun([], baseDirectory));

    [Fact]
    public void BundleContentsPointsAtTheContentsFolder() =>
        Assert.Equal("/Applications/Perene TTS.app/Contents", DesktopLaunch.BundleContents("/Applications/Perene TTS.app/Contents/Resources/dotnet/"));
}
