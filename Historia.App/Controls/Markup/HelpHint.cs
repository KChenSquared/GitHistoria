namespace Historia.Controls;

/// <summary>
/// A small circular "?" badge for AXAML: <c>&lt;ctl:HelpHint Text="…" /&gt;</c>. Hovering shows
/// <see cref="Text"/> in a tooltip that wraps to fit the message with no scrollbar; a
/// <c>"\n\n"</c> in the text renders as a paragraph break. The badge is not focusable, so the text
/// is also exposed as its automation help text for screen readers. The control itself carries the
/// <see cref="ViewKit.HelpHintClass"/> style class, the tooltip and the automation values.
/// </summary>
public sealed class HelpHint : ContentControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<HelpHint, string?>(nameof(Text));

    public static readonly StyledProperty<double> DiameterProperty =
        AvaloniaProperty.Register<HelpHint, double>(nameof(Diameter), 15d);

    private readonly TextBlock _mark;

    static HelpHint()
    {
        TextProperty.Changed.AddClassHandler<HelpHint>((h, _) => h.ApplyText());
        DiameterProperty.Changed.AddClassHandler<HelpHint>((h, _) => h.ApplyDiameter());
    }

    public HelpHint()
    {
        _mark = new TextBlock
        {
            Text = "?",
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");

        Classes.Add(HelpHintClass);
        Content = _mark;
        BorderThickness = new Thickness(1);
        Background = Brushes.Transparent;
        Padding = default;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = new Cursor(StandardCursorType.Help);
        this.Dyn(BorderBrushProperty, "ColorBorder");
        Avalonia.Automation.AutomationProperties.SetName(this, "Help");
        ToolTip.SetShowDelay(this, 200);
        ApplyDiameter();
    }

    /// <summary>The hint shown on hover.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Width and height of the badge in pixels.</summary>
    public double Diameter
    {
        get => GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    private void ApplyDiameter()
    {
        var d = Diameter;
        Width = d;
        Height = d;
        CornerRadius = new CornerRadius(d / 2);
        _mark.FontSize = d * 0.72;
    }

    private void ApplyText()
    {
        var text = Text;
        if (string.IsNullOrEmpty(text))
        {
            ToolTip.SetTip(this, null);
            Avalonia.Automation.AutomationProperties.SetHelpText(this, string.Empty);
            return;
        }

        // A plain wrapping TextBlock sizes itself to the text (up to MaxWidth) with no
        // ScrollViewer in the way, so every message shows whole and right-sized.
        ToolTip.SetTip(this, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        Avalonia.Automation.AutomationProperties.SetHelpText(this, text);
    }
}
