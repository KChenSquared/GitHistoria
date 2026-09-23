using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using HistoriaApp.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace HistoriaApp.Tests;

/// <summary>
/// Headless Avalonia bootstrap for the App test suite (debloat audit §4, Path B). Minimal on
/// purpose: a bare <see cref="Application"/> with the Fluent theme + Inter fonts so controls
/// construct and resolve their <c>DynamicResource</c> keys, but none of the real
/// <c>Historia.App</c> startup / DI. Tests construct a control, drive its logic, and
/// assert — no pixel or snapshot assertions (that layer is still deferred; see
/// <c>Historia-DevDocs/todo/add-historia-app-tests-project.md</c>).
/// </summary>
public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .WithInterFont();
}

/// <summary>The throwaway <see cref="Application"/> headless tests run inside.</summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Historia.App.Tests/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
        });
    }
}
