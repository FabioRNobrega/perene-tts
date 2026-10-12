using PereneTts.Desktop;

namespace WebApp.Tests;

public sealed class DesktopLaunchTests
{
    [Fact]
    public void DesktopArgumentSelectsDesktopMode() =>
        Assert.True(DesktopLaunch.ShouldRun(["--desktop"], "/workspace/WebApp/bin/Debug/net10.0/"));

    [Theory]
    [InlineData("/Applications/Perene TTS.app/Contents/MacOS/")]
    [InlineData("/Applications/Perene TTS.app/Contents/MacOS")]
    [InlineData("/private/var/folders/x/AppTranslocation/1234/d/Perene TTS.app/Contents/MacOS/")]
    public void AppBundlePathSelectsDesktopModeWithoutArguments(string baseDirectory) =>
        Assert.True(DesktopLaunch.ShouldRun([], baseDirectory));

    [Theory]
    [InlineData("/workspace/WebApp/bin/Debug/net10.0/")]
    [InlineData("/app/")]
    [InlineData("/Users/ana/MacOS/")]
    [InlineData("/Users/ana/Contents/MacOS/")]
    [InlineData("/Users/ana/.app/Contents/MacOS/")]
    public void OtherDirectoriesKeepWebMode(string baseDirectory) =>
        Assert.False(DesktopLaunch.ShouldRun([], baseDirectory));

    [Fact]
    public void BundleContentsPointsAtTheContentsFolder() =>
        Assert.Equal("/Applications/Perene TTS.app/Contents", DesktopLaunch.BundleContents("/Applications/Perene TTS.app/Contents/MacOS/"));
}
