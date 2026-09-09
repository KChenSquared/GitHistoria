using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace HistoriaApp.Tests;

/// <summary>Proves the headless Avalonia harness itself works — a UI-thread test can construct
/// a themed control and run a layout pass. If this fails, nothing else in the suite is
/// meaningful.</summary>
public class HeadlessHarnessTests
{
    [AvaloniaFact]
    public void Headless_platform_runs_a_layout_pass_on_the_ui_thread()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess());

        var panel = new StackPanel { Children = { new TextBlock { Text = "hello" }, new Button { Content = "ok" } } };
        panel.Measure(new Avalonia.Size(400, 300));
        panel.Arrange(new Avalonia.Rect(panel.DesiredSize));

        Assert.True(panel.Bounds.Width > 0);
        Assert.True(double.IsFinite(panel.DesiredSize.Height));
    }
}
