using Avalonia.Controls.Documents;
using Historia.Services.Markdown;
using static Historia.Views.ViewKit;

namespace Historia.Views;

/// <summary>
/// Renders the <see cref="MarkdownRenderer.ToBlocks"/> model to native Avalonia controls
/// (V4 port of MarkdownView.razor — no HTML, no WebView). Links open via
/// <see cref="IExternalLauncher"/>.
/// </summary>
public sealed class MarkdownView : UserControl
{
    private static readonly FontFamily Mono = new("Consolas, Menlo, monospace");

    public MarkdownView(string? markdown, IExternalLauncher launcher)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in MarkdownRenderer.ToBlocks(markdown))
            panel.Children.Add(RenderBlock(block, launcher));
        Content = panel;
    }

    private static Control RenderBlock(MdBlock block, IExternalLauncher launcher) => block switch
    {
        MdHeading h => Para(h.Inlines, launcher, size: 20 - Math.Min(6, h.Level) * 1.5, weight: FontWeight.SemiBold),
        MdParagraph p => Para(p.Inlines, launcher),
        MdRule => new Border { Height = 1, Margin = new Thickness(0, 6) }.Dyn(Border.BackgroundProperty, "ColorBorder"),
        MdCode c => new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(4),
            Child = new SelectableTextBlock { Text = c.Text, FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap },
        }.Dyn(Border.BackgroundProperty, "ColorSurface2"),
        MdQuote q => new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            Child = new StackPanel { Spacing = 6, Children = { } }.Also(sp =>
            {
                foreach (var child in q.Children) sp.Children.Add(RenderBlock(child, launcher));
            }),
        }.Dyn(Border.BorderBrushProperty, "ColorBorderStrong"),
        MdList l => RenderList(l, launcher),
        MdTable t => RenderTable(t, launcher),
        _ => new Control(),
    };

    private static Control RenderList(MdList l, IExternalLauncher launcher)
    {
        var sp = new StackPanel { Spacing = 3, Margin = new Thickness(6, 0, 0, 0) };
        for (var i = 0; i < l.Items.Count; i++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(Text(l.Ordered ? $"{i + 1}." : "•", "ColorTextMuted", 12));
            var content = Para(l.Items[i], launcher);
            content.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            sp.Children.Add(row);
        }
        return sp;
    }

    private static Control RenderTable(MdTable t, IExternalLauncher launcher)
    {
        var grid = new Grid();
        var cols = Math.Max(1, t.Header.Count);
        for (var c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        void AddRow(int r, IReadOnlyList<IReadOnlyList<MdInline>> cells, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < cols && c < cells.Count; c++)
            {
                var cell = Para(cells[c], launcher, weight: header ? FontWeight.SemiBold : FontWeight.Normal);
                cell.Margin = new Thickness(8, 4);
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        AddRow(0, t.Header, header: true);
        for (var r = 0; r < t.Rows.Count; r++)
            AddRow(r + 1, t.Rows[r], header: false);

        return new Border { Child = grid, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) }
            .Dyn(Border.BorderBrushProperty, "ColorBorder");
    }

    private static TextBlock Para(IReadOnlyList<MdInline> inlines, IExternalLauncher launcher,
        double size = 13, FontWeight weight = FontWeight.Normal)
    {
        var tb = new SelectableTextBlock { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap }
            .Dyn(SelectableTextBlock.ForegroundProperty, "ColorText");
        foreach (var run in inlines)
        {
            switch (run.Kind)
            {
                case MdInlineKind.Code:
                    tb.Inlines!.Add(new Run(run.Text) { FontFamily = Mono });
                    break;
                case MdInlineKind.Bold:
                    tb.Inlines!.Add(new Run(run.Text) { FontWeight = FontWeight.SemiBold });
                    break;
                case MdInlineKind.Italic:
                    tb.Inlines!.Add(new Run(run.Text) { FontStyle = FontStyle.Italic });
                    break;
                case MdInlineKind.Strike:
                    tb.Inlines!.Add(new Run(run.Text) { TextDecorations = TextDecorations.Strikethrough });
                    break;
                case MdInlineKind.Link:
                    var link = new Run(run.Text) { TextDecorations = TextDecorations.Underline };
                    link.Bind(Run.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ColorAccent"));
                    tb.Inlines!.Add(link);
                    var href = run.Href!;
                    tb.PointerPressed += (_, e) =>
                    {
                        if (e.GetCurrentPoint(tb).Properties.IsLeftButtonPressed)
                            launcher.OpenUrl(href);
                    };
                    break;
                default:
                    tb.Inlines!.Add(new Run(run.Text));
                    break;
            }
        }
        return tb;
    }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T self, Action<T> act) { act(self); return self; }
}
