namespace Historia.Views;

/// <summary>
/// The numbered remote cloud: a filled cloud silhouette with the remote's number knocked out of
/// it. The silhouette is the outer outline of the vendored <c>cloud</c> glyph, so no extra icon
/// is vendored. The digit sits in the cloud's lower, wider lobe and is drawn in the surface
/// colour behind the icon (or the badge's lane colour on the graph), so it reads as a hole.
/// </summary>
internal static class RemoteCloudGlyph
{
    /// <summary>The <c>cloud</c> glyph's path data up to and including its first <c>Z</c>: the
    /// outer outline only, which fills as a solid cloud.</summary>
    internal static string FilledPathData { get; } = OuterSubpath(IconPaths.Get("cloud"));

    private static string OuterSubpath(string data)
    {
        var z = data.IndexOf('Z');
        return z < 0 ? data : data[..(z + 1)];
    }

    /// <summary>A filled cloud on the 960-unit grid, scaled to a <paramref name="width"/>-square
    /// box. Callers cache the result.</summary>
    internal static Geometry CreateFilledGeometry(double width)
    {
        var g = Geometry.Parse(FilledPathData);
        g.Transform = new MatrixTransform(
            Matrix.CreateTranslation(0, 960) * Matrix.CreateScale(width / 960.0, width / 960.0));
        return g;
    }

    /// <summary>The band, relative to the icon's top-left, the digit is centred in. The cloud
    /// spans grid rows 160–800; the band covers its body below the dome's shoulders.</summary>
    internal static Rect DigitBand(double width) =>
        new(width * 0.16, width * 0.36, width * 0.70, width * 0.47);

    /// <summary>The digit's font size: two-digit numbers get a smaller size so they still fit
    /// inside the cloud.</summary>
    internal static double DigitFontSize(int number, double width) =>
        number >= 10 ? width * 0.47 : width * 0.62;

    /// <summary>A <paramref name="width"/>-square icon: the filled cloud in
    /// <paramref name="cloudKey"/> and, when <paramref name="number"/> is positive, the number in
    /// <paramref name="knockoutKey"/>. A zero number gives a plain filled cloud.</summary>
    internal static Control CreateIcon(int number, double width, string cloudKey, string knockoutKey)
    {
        var panel = new Panel { Width = width, Height = width, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new Path
        {
            Data = CreateFilledGeometry(width),
            Stretch = Stretch.None,
            Width = width,
            Height = width,
        }.Dyn(Path.FillProperty, cloudKey));

        if (number > 0)
        {
            var band = DigitBand(width);
            var digit = new TextBlock
            {
                Text = number.ToString(CultureInfo.InvariantCulture),
                FontSize = DigitFontSize(number, width),
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }.Dyn(TextBlock.ForegroundProperty, knockoutKey);
            panel.Children.Add(new Border
            {
                Child = digit,
                Width = band.Width,
                Height = band.Height,
                Margin = new Thickness(band.X, band.Y, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            });
        }
        return panel;
    }
}
