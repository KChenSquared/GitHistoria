namespace Historia.Tests;

/// <summary>The detail router's view model: route parsing, the reported route, shortcuts and
/// navigation requests from the bus.</summary>
[Trait("tier", "unit")]
public sealed class DetailRouterViewModelTests
{
    private readonly NavigationBus _bus = new();
    private readonly KeyboardCommandRouter _keyboard = new();

    private DetailRouterViewModel Create(IUiDispatcher? ui = null)
        => new(_bus, _keyboard, ui ?? new ImmediateUiDispatcher());

    [Theory]
    [InlineData("", DetailPage.Home)]
    [InlineData("home", DetailPage.Home)]
    [InlineData("something-unknown", DetailPage.Home)]
    [InlineData("repo", DetailPage.Repo)]
    [InlineData("settings", DetailPage.Settings)]
    [InlineData("themes", DetailPage.Themes)]
    [InlineData("repo-settings", DetailPage.RepoSettings)]
    [InlineData("rebase", DetailPage.Rebase)]
    [InlineData("dev", DetailPage.Developer)]
    [InlineData("profiles", DetailPage.Profiles)]
    public void A_route_head_names_its_page(string route, DetailPage expected)
        => Assert.Equal(expected, DetailRouterViewModel.Parse(route).Page);

    [Fact]
    public void A_leading_slash_is_dropped_and_the_query_is_kept_in_the_path()
    {
        var route = DetailRouterViewModel.Parse("/profiles?intent=add");

        Assert.Equal(DetailPage.Profiles, route.Page);
        Assert.Equal("profiles?intent=add", route.Path);
        Assert.Equal("add", route.Intent);
        Assert.Equal("", route.Onto);
    }

    [Fact]
    public void The_head_ignores_the_query_and_a_null_route_is_home()
    {
        Assert.Equal(DetailPage.Home, DetailRouterViewModel.Parse("?intent=clone").Page);
        Assert.Equal("clone", DetailRouterViewModel.Parse("?intent=clone").Intent);

        var home = DetailRouterViewModel.Parse(null);
        Assert.Equal(DetailPage.Home, home.Page);
        Assert.Equal("", home.Path);
    }

    [Fact]
    public void The_rebase_target_is_unescaped()
    {
        var route = DetailRouterViewModel.Parse("rebase?onto=feature%2Fx%20y");

        Assert.Equal(DetailPage.Rebase, route.Page);
        Assert.Equal("feature/x y", route.Onto);
    }

    [Theory]
    [InlineData("home?intent=clone", "intent", "clone")]
    [InlineData("home?a=1&intent=clone&b=2", "intent", "clone")]
    [InlineData("home?a=1&b=2", "intent", "")]
    [InlineData("home", "intent", "")]
    [InlineData("home?intent", "intent", "")]
    [InlineData("home?intent=a=b", "intent", "a=b")]
    [InlineData("home?&&intent=x&&", "intent", "x")]
    [InlineData("home?intent=a%26b", "intent", "a&b")]
    public void A_query_value_is_found_by_key(string route, string key, string expected)
        => Assert.Equal(expected, DetailRouterViewModel.QueryValue(route, key));

    [Fact]
    public void The_bus_route_decides_the_first_page_and_is_reported_back_normalized()
    {
        _bus.ReportRoute("repo");
        var reported = new List<string>();
        _bus.RouteChanged += reported.Add;

        var vm = Create();

        Assert.Equal(DetailPage.Repo, vm.Current.Page);
        Assert.Empty(reported);

        vm.Navigate("/settings");

        Assert.Equal(DetailPage.Settings, vm.Current.Page);
        Assert.Equal(["settings"], reported);
        Assert.Equal("settings", _bus.CurrentRoute);
    }

    [Fact]
    public void Navigating_to_the_page_already_shown_still_announces_it()
    {
        var vm = Create();
        var seen = new List<DetailRoute>();
        vm.Navigated += (_, route) => seen.Add(route);

        vm.Navigate("settings");
        vm.Navigate("settings");

        Assert.Equal(2, seen.Count);
        Assert.All(seen, r => Assert.Equal(DetailPage.Settings, r.Page));
    }

    [Fact]
    public void A_navigation_request_from_the_bus_is_applied_on_the_ui_thread()
    {
        var ui = new RecordingUiDispatcher();
        var vm = Create(ui);

        _bus.RequestNavigate("themes");

        Assert.Equal(DetailPage.Home, vm.Current.Page);
        Assert.Equal(1, ui.PostCount);

        ui.DrainPosted();

        Assert.Equal(DetailPage.Themes, vm.Current.Page);
    }

    [Fact]
    public async Task The_settings_and_home_shortcuts_navigate()
    {
        var vm = Create();

        Assert.True(await _keyboard.DispatchAsync("Ctrl+,"));
        Assert.Equal(DetailPage.Settings, vm.Current.Page);

        Assert.True(await _keyboard.DispatchAsync("Ctrl+B"));
        Assert.Equal(DetailPage.Home, vm.Current.Page);
    }

    [Fact]
    public async Task Dispose_unregisters_the_shortcuts_and_stops_listening_to_the_bus()
    {
        var vm = Create();
        var navigated = 0;
        vm.Navigated += (_, _) => navigated++;

        vm.Dispose();

        Assert.False(_keyboard.IsRegistered("Ctrl+,"));
        Assert.False(_keyboard.IsRegistered("Ctrl+B"));
        Assert.False(await _keyboard.DispatchAsync("Ctrl+,"));
        _bus.RequestNavigate("themes");
        vm.Navigate("settings");
        Assert.Equal(0, navigated);
        Assert.Equal(DetailPage.Home, vm.Current.Page);
    }

    [Fact]
    public void A_request_posted_before_dispose_does_nothing_afterwards()
    {
        var ui = new RecordingUiDispatcher();
        var vm = Create(ui);
        _bus.RequestNavigate("themes");

        vm.Dispose();
        ui.DrainPosted();

        Assert.Equal(DetailPage.Home, vm.Current.Page);
    }
}
