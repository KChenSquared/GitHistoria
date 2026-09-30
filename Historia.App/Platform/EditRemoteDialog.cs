namespace Historia.Platform;

/// <summary>The values chosen in the Edit remote dialog. <paramref name="ResetPushUrl"/> is true
/// when the user asked to drop the separate push URL.</summary>
internal sealed record EditRemoteResult(string Name, string Url, bool ResetPushUrl);

/// <summary>
/// Modal "Edit remote" window: the remote's name, its fetch URL and, only when a separate push
/// URL is configured, a checkbox to reset it. Save stays disabled until the input is valid;
/// Cancel is always the rightmost button.
/// </summary>
internal static class EditRemoteDialog
{
    /// <summary>Why <paramref name="name"/> / <paramref name="url"/> cannot be saved, or
    /// <see langword="null"/> when both are acceptable. <paramref name="otherNames"/> are the
    /// names of the repository's other remotes (compared ordinally).</summary>
    internal static string? Validate(string name, string url, IReadOnlyCollection<string> otherNames)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a name for the remote.";
        if (name.Any(char.IsWhiteSpace))
            return "A remote name cannot contain whitespace.";
        if (name.StartsWith('/') || name.EndsWith('/'))
            return "A remote name cannot start or end with '/'.";
        if (name.Contains("..", StringComparison.Ordinal))
            return "A remote name cannot contain '..'.";
        if (otherNames.Contains(name, StringComparer.Ordinal))
            return $"A remote named '{name}' already exists.";

        if (string.IsNullOrWhiteSpace(url))
            return "Enter a URL for the remote.";
        if (url.Any(char.IsWhiteSpace))
            return "A remote URL cannot contain whitespace.";
        return null;
    }

    /// <summary>Shows the dialog for <paramref name="remote"/>. Returns <see langword="null"/>
    /// when the user cancels or saves without changing anything.</summary>
    public static async Task<EditRemoteResult?> ShowAsync(RemoteInfo remote, IReadOnlyCollection<string> otherNames)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(remote, otherNames));

        var nameBox = new TextBox { Text = remote.Name, MinWidth = 460 };
        var urlBox = new TextBox { Text = remote.Url, MinWidth = 460 };
        var hint = new TextBlock { FontSize = 11, Margin = new Thickness(0, 6, 0, 0), IsVisible = false, TextWrapping = TextWrapping.Wrap }
            .Dyn(TextBlock.ForegroundProperty, "ColorDanger");

        CheckBox? resetPush = null;
        if (remote.HasDistinctPushUrl)
            resetPush = new CheckBox
            {
                Content = $"Reset push URL (currently {remote.PushUrl})",
                IsChecked = false,
                Margin = new Thickness(0, 12, 0, 0),
            };

        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 80, IsEnabled = false };
        save.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };

        void Revalidate()
        {
            var problem = Validate((nameBox.Text ?? "").Trim(), (urlBox.Text ?? "").Trim(), otherNames);
            hint.Text = problem;
            hint.IsVisible = problem is not null;
            save.IsEnabled = problem is null;
        }
        nameBox.TextChanged += (_, _) => Revalidate();
        urlBox.TextChanged += (_, _) => Revalidate();

        static TextBlock Label(string t) => new()
        {
            Text = t,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 10, 0, 3),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0),
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label("Name"));
        panel.Children.Add(nameBox);
        panel.Children.Add(Label("URL"));
        panel.Children.Add(urlBox);
        if (resetPush is not null)
            panel.Children.Add(resetPush);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "Edit remote",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var tcs = new TaskCompletionSource<EditRemoteResult?>();
        save.Click += (_, _) =>
        {
            var name = (nameBox.Text ?? "").Trim();
            var url = (urlBox.Text ?? "").Trim();
            var reset = resetPush?.IsChecked == true;
            var unchanged = string.Equals(name, remote.Name, StringComparison.Ordinal)
                && string.Equals(url, remote.Url, StringComparison.Ordinal)
                && !reset;
            tcs.TrySetResult(unchanged ? null : new EditRemoteResult(name, url, reset));
            window.Close();
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(null);
        window.Opened += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };

        Revalidate();
        await SimpleDialog.ShowOwnedOrFree(window, AvaloniaDialogService.Owner);
        return await tcs.Task;
    }
}
