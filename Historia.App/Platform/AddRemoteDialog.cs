namespace Historia.Platform;

/// <summary>The values chosen in the Add remote dialog. <paramref name="Fetch"/> is true when the
/// new remote should be fetched right after it is added.</summary>
internal sealed record AddRemoteResult(string Name, string Url, bool Fetch);

/// <summary>
/// Modal "Add remote" window: a name, a fetch URL and a "Fetch after adding" checkbox (on by
/// default). Validation is shared with the Edit remote dialog; Add stays disabled until the
/// input is valid. Cancel is always the rightmost button.
/// </summary>
internal static class AddRemoteDialog
{
    /// <summary>Shows the dialog. <paramref name="existing"/> are the repository's remotes: their
    /// names and URLs may not be reused, and the name starts as <c>origin</c> when there are none.
    /// Returns <see langword="null"/> when the user cancels.</summary>
    public static async Task<AddRemoteResult?> ShowAsync(IReadOnlyList<RemoteInfo> existing)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(existing));

        var otherNames = existing.Select(r => r.Name).ToList();
        var otherUrls = EditRemoteDialog.UrlOwners(existing);

        var nameBox = new TextBox { Text = existing.Count == 0 ? "origin" : "", MinWidth = 460, Watermark = "Remote name" };
        var urlBox = new TextBox { MinWidth = 460, Watermark = "git@host:owner/repo.git" };
        var fetch = new CheckBox { Content = "Fetch after adding", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
        var hint = new TextBlock { FontSize = 11, Margin = new Thickness(0, 6, 0, 0), IsVisible = false, TextWrapping = TextWrapping.Wrap }
            .Dyn(TextBlock.ForegroundProperty, "ColorDanger");

        var add = new Button { Content = "Add", IsDefault = true, MinWidth = 80, IsEnabled = false };
        add.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };

        // An empty form is not an error yet: Add stays disabled, but no hint shows until the
        // user has typed something.
        void Revalidate()
        {
            var name = (nameBox.Text ?? "").Trim();
            var url = (urlBox.Text ?? "").Trim();
            var problem = EditRemoteDialog.Validate(name, url, otherNames, otherUrls);
            var untouched = url.Length == 0 && (name.Length == 0 || existing.Count == 0);
            hint.Text = problem;
            hint.IsVisible = problem is not null && !untouched;
            add.IsEnabled = problem is null;
        }
        nameBox.TextChanged += (_, _) => Revalidate();
        urlBox.TextChanged += (_, _) => Revalidate();

        var buttons = EditRemoteDialog.ButtonRow(add, cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(EditRemoteDialog.FieldLabel("Name"));
        panel.Children.Add(nameBox);
        panel.Children.Add(EditRemoteDialog.FieldLabel("URL"));
        panel.Children.Add(urlBox);
        panel.Children.Add(fetch);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "Add remote",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var tcs = new TaskCompletionSource<AddRemoteResult?>();
        add.Click += (_, _) =>
        {
            tcs.TrySetResult(new AddRemoteResult(
                (nameBox.Text ?? "").Trim(), (urlBox.Text ?? "").Trim(), fetch.IsChecked == true));
            window.Close();
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(null);
        window.Opened += (_, _) =>
        {
            if (existing.Count == 0)
                urlBox.Focus();
            else
                nameBox.Focus();
        };

        Revalidate();
        await SimpleDialog.ShowOwnedOrFree(window, AvaloniaDialogService.Owner);
        return await tcs.Task;
    }
}
