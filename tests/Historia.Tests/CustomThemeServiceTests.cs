using System.Text.Json;
using Historia.Models;
using Historia.Services.Graph;
using Historia.Services.Storage;
using Historia.Services.Theming;
using Xunit;

namespace Historia.Tests;

/// <summary>§V6-G9: per-file custom themes — round-trip, validation, base inheritance, delete,
/// and the pure <see cref="CustomThemeService.Compose"/> overlay.</summary>
public sealed class CustomThemeServiceTests : IDisposable
{
    private readonly StateDir _dir = new();
    private readonly CustomThemeService _svc = new();

    public void Dispose() => _dir.Dispose();

    private static CustomTheme Sample(string id = "ocean") => new()
    {
        Id = id,
        Name = "Ocean",
        Base = "Dark",
        Tokens = new Dictionary<string, string> { ["bg"] = "#0B1E2D", ["primary"] = "#3FA9F5" },
        Lane = ["#FF0000", null, "#00FF00"],
    };

    [Fact]
    public async Task Round_trips_a_theme_file()
    {
        await _svc.SaveAsync(Sample());

        var list = _svc.List();
        var got = Assert.Single(list);
        Assert.Equal("ocean", got.Id);
        Assert.Equal("Ocean", got.Name);
        Assert.Equal(Rgb.Parse("#0B1E2D"), Rgb.Parse(got.Tokens["bg"]));
        Assert.Equal(_svc.Get("ocean"), got);
        Assert.True(File.Exists(AppPaths.CustomThemeFile("ocean")));
    }

    [Fact]
    public void Compose_over_an_empty_override_equals_the_base_builtin()
    {
        var bare = new CustomTheme { Id = "x", Name = "X", Base = "Light" };
        var resolved = _svc.Compose(bare);

        var baseTokens = HistoriaPalette.For(EffectiveTheme.Light);
        foreach (var token in HistoriaPalette.Tokens)
            Assert.Equal(baseTokens[token], resolved.Tokens[token]);

        Assert.Equal(LanePalette.Size, resolved.Lanes.Count);
        for (var i = 0; i < LanePalette.Size; i++)
            Assert.Equal(LanePalette.ColorFor(i, EffectiveTheme.Light), resolved.Lanes[i]);
    }

    [Fact]
    public void Compose_applies_token_and_lane_overrides_and_inherits_the_rest()
    {
        var resolved = _svc.Compose(Sample());

        Assert.Equal(Rgb.Parse("#0B1E2D"), resolved.Tokens["bg"]);
        Assert.Equal(Rgb.Parse("#3FA9F5"), resolved.Tokens["primary"]);
        // a token not mentioned falls through to the Dark base
        Assert.Equal(HistoriaPalette.Get(EffectiveTheme.Dark, "text"), resolved.Tokens["text"]);

        Assert.Equal(Rgb.Parse("#FF0000"), resolved.Lanes[0]);
        Assert.Equal(LanePalette.ColorFor(1, EffectiveTheme.Dark), resolved.Lanes[1]); // null -> inherit
        Assert.Equal(Rgb.Parse("#00FF00"), resolved.Lanes[2]);
        Assert.Equal(LanePalette.ColorFor(5, EffectiveTheme.Dark), resolved.Lanes[5]); // past the override list
    }

    [Fact]
    public void An_unparseable_hex_makes_the_theme_skipped_not_thrown()
    {
        Directory.CreateDirectory(AppPaths.ThemesDirectory);
        File.WriteAllText(AppPaths.CustomThemeFile("broken"),
            JsonSerializer.Serialize(new CustomTheme
            {
                Id = "broken",
                Name = "Broken",
                Tokens = new Dictionary<string, string> { ["bg"] = "not-a-colour" },
            }, HistoriaJson.Options));

        Assert.Empty(_svc.List());
        Assert.Null(_svc.Get("broken"));
    }

    [Fact]
    public void Unknown_token_keys_are_dropped_on_compose()
    {
        var t = new CustomTheme
        {
            Id = "u", Name = "U",
            Tokens = new Dictionary<string, string> { ["not-a-real-token"] = "#123456", ["bg"] = "#222222" },
        };
        var resolved = _svc.Compose(t);

        Assert.False(resolved.Tokens.ContainsKey("not-a-real-token"));
        Assert.Equal(Rgb.Parse("#222222"), resolved.Tokens["bg"]);
    }

    [Fact]
    public async Task DeleteAsync_removes_only_that_file()
    {
        await _svc.SaveAsync(Sample("a"));
        await _svc.SaveAsync(Sample("b") with { Name = "Bee" });
        Assert.Equal(2, _svc.List().Count);

        await _svc.DeleteAsync("a");

        var remaining = Assert.Single(_svc.List());
        Assert.Equal("b", remaining.Id);
        Assert.False(File.Exists(AppPaths.CustomThemeFile("a")));
        Assert.True(File.Exists(AppPaths.CustomThemeFile("b")));
    }

    [Fact]
    public async Task Save_and_delete_raise_Changed()
    {
        var hits = 0;
        _svc.Changed += (_, _) => hits++;

        await _svc.SaveAsync(Sample());
        await _svc.DeleteAsync("ocean");

        Assert.Equal(2, hits);
    }

    [Fact]
    public void NewFrom_produces_a_distinct_id_and_the_requested_base()
    {
        var a = _svc.NewFrom("Light");
        var b = _svc.NewFrom("Dark");

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal("Light", a.Base);
        Assert.Equal(EffectiveTheme.Light, a.BaseTheme);
        Assert.Equal("Dark", b.Base);
    }

    [Fact]
    public void NewFrom_imports_the_matching_built_in_splash()
    {
        var light = _svc.NewFrom("Light");
        var dark = _svc.NewFrom("Dark");

        Assert.Equal("avares://Historia/Assets/splash-light.png", light.SplashImagePath);
        Assert.Equal("avares://Historia/Assets/splash-dark.png", dark.SplashImagePath);
        Assert.Equal(2, light.SplashSeconds);
    }

    [Fact]
    public async Task Round_trips_the_splash_fields()
    {
        await _svc.SaveAsync(Sample() with { SplashImagePath = @"C:\art\my-splash.png", SplashSeconds = 7 });

        var got = _svc.Get("ocean")!;
        Assert.Equal(@"C:\art\my-splash.png", got.SplashImagePath);
        Assert.Equal(7, got.SplashSeconds);
    }
}
