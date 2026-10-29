using Kurobane.Core;
using Xunit;

namespace Kurobane.Tests;

public class ThemeCatalogTests
{
    [Fact]
    public void Eight_theme_families_each_with_light_and_dark()
    {
        Assert.Equal(8, ThemeCatalog.All.Count);
        Assert.All(ThemeCatalog.All, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Id));
            Assert.False(string.IsNullOrWhiteSpace(t.DisplayName));
            Assert.StartsWith("#", t.Light.Bg);
            Assert.StartsWith("#", t.Dark.Bg);
            Assert.NotEqual(t.Light.Bg, t.Dark.Bg);
        });
    }

    [Fact]
    public void Unknown_id_falls_back_to_the_first_theme()
    {
        Assert.Equal(ThemeCatalog.All[0].Id, ThemeCatalog.Get("no-such-theme").Id);
        Assert.Equal("washi-ink", ThemeCatalog.Get("WASHI-INK").Id); // case-insensitive
    }

    [Theory]
    [InlineData("light", true, true)]
    [InlineData("light", false, true)]
    [InlineData("dark", true, false)]
    [InlineData("dark", false, false)]
    [InlineData("system", true, false)]  // system + dark OS => dark
    [InlineData("system", false, true)]   // system + light OS => light
    public void Appearance_resolves_against_the_os(string mode, bool osDark, bool expectLight)
    {
        var m = Enum.Parse<AppearanceMode>(mode, true);
        Assert.Equal(expectLight, AppearanceResolver.Resolve(m, osDark));
    }

    [Fact]
    public void Reduced_motion_zeroes_panel_animations()
    {
        Assert.Equal(TimeSpan.Zero, MotionPolicy.PanelDuration(true));
        Assert.True(MotionPolicy.PanelDuration(false) > TimeSpan.Zero);
    }
}
