namespace Historia.Views;

/// <summary>A minimal titled page for routes whose full view is not yet ported.</summary>
public sealed class SimplePageView : UserControl
{
    public SimplePageView(string title, string body)
    {
        var stack = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(32) };
        stack.Children.Add(Text(title, "ColorText", 20, Avalonia.Media.FontWeight.SemiBold));
        stack.Children.Add(Text(body, "ColorTextMuted", 13));
        Content = stack;
        this.Dyn(BackgroundProperty, "ColorChrome");
    }
}
