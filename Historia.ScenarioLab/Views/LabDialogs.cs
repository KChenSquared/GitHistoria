namespace Historia.ScenarioLab.Views;

public enum SaveDiscardCancelResult
{
    Save,
    Discard,
    Cancel
}

/// <summary>
/// Code-built modal dialogs for confirmations, prompts, and unsaved changes.
/// </summary>
public static class LabDialogs
{
    public static async Task<bool> ConfirmAsync(
        Window owner,
        string title,
        string message,
        string confirmText = "Confirm",
        bool danger = false)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            CanResize = false,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        dialog.Bind(Window.BackgroundProperty, new DynamicResourceExtension("ColorChrome"));

        var grid = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("*,Auto")
        };

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 20)
        };
        messageText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ColorText"));
        grid.Children.Add(messageText);

        var buttonBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        Grid.SetRow(buttonBar, 1);

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true
        };
        cancelButton.Click += (_, _) => dialog.Close(false);

        var confirmButton = new Button
        {
            Content = confirmText,
            IsDefault = true
        };
        if (danger)
        {
            confirmButton.Classes.Add("danger");
        }
        else
        {
            confirmButton.Classes.Add("primary");
        }
        confirmButton.Click += (_, _) => dialog.Close(true);

        buttonBar.Children.Add(cancelButton);
        buttonBar.Children.Add(confirmButton);
        grid.Children.Add(buttonBar);

        dialog.Content = grid;

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close(false);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                dialog.Close(true);
                e.Handled = true;
            }
        };

        return await dialog.ShowDialog<bool>(owner);
    }

    public static async Task<string?> PromptAsync(
        Window owner,
        string title,
        string label,
        string initial = "",
        Func<string, string?>? validate = null)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            CanResize = false,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        dialog.Bind(Window.BackgroundProperty, new DynamicResourceExtension("ColorChrome"));

        var grid = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto")
        };

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8)
        };
        labelText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ColorText"));
        grid.Children.Add(labelText);

        var textBox = new TextBox
        {
            Text = initial,
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(textBox, 1);
        grid.Children.Add(textBox);

        var errorText = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 16),
            IsVisible = false
        };
        errorText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ColorDanger"));
        Grid.SetRow(errorText, 2);
        grid.Children.Add(errorText);

        var buttonBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        Grid.SetRow(buttonBar, 3);

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true
        };
        cancelButton.Click += (_, _) => dialog.Close(null);

        var okButton = new Button
        {
            Content = "OK",
            IsDefault = true
        };
        okButton.Classes.Add("primary");
        okButton.Click += (_, _) =>
        {
            var text = textBox.Text?.Trim() ?? string.Empty;
            if (validate is not null)
            {
                var err = validate(text);
                if (!string.IsNullOrEmpty(err))
                {
                    errorText.Text = err;
                    errorText.IsVisible = true;
                    return;
                }
            }
            dialog.Close(text);
        };

        buttonBar.Children.Add(cancelButton);
        buttonBar.Children.Add(okButton);
        grid.Children.Add(buttonBar);

        void RunValidation()
        {
            if (validate is null) return;
            var text = textBox.Text?.Trim() ?? string.Empty;
            var err = validate(text);
            if (string.IsNullOrEmpty(err))
            {
                errorText.IsVisible = false;
                errorText.Text = string.Empty;
                okButton.IsEnabled = true;
            }
            else
            {
                errorText.Text = err;
                errorText.IsVisible = true;
                okButton.IsEnabled = false;
            }
        }

        textBox.TextChanged += (_, _) => RunValidation();
        RunValidation();

        dialog.Content = grid;

        dialog.Opened += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && okButton.IsEnabled)
            {
                var text = textBox.Text?.Trim() ?? string.Empty;
                dialog.Close(text);
                e.Handled = true;
            }
        };

        return await dialog.ShowDialog<string?>(owner);
    }

    public static async Task<SaveDiscardCancelResult> SaveDiscardCancelAsync(
        Window owner,
        string fileName)
    {
        var dialog = new Window
        {
            Title = "Unsaved Changes",
            Width = 460,
            CanResize = false,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        dialog.Bind(Window.BackgroundProperty, new DynamicResourceExtension("ColorChrome"));

        var grid = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("*,Auto")
        };

        var messageText = new TextBlock
        {
            Text = $"Do you want to save changes to '{fileName}'?",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 20)
        };
        messageText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ColorText"));
        grid.Children.Add(messageText);

        var buttonBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        Grid.SetRow(buttonBar, 1);

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true
        };
        cancelButton.Click += (_, _) => dialog.Close(SaveDiscardCancelResult.Cancel);

        var discardButton = new Button
        {
            Content = "Discard"
        };
        discardButton.Classes.Add("danger");
        discardButton.Click += (_, _) => dialog.Close(SaveDiscardCancelResult.Discard);

        var saveButton = new Button
        {
            Content = "Save",
            IsDefault = true
        };
        saveButton.Classes.Add("primary");
        saveButton.Click += (_, _) => dialog.Close(SaveDiscardCancelResult.Save);

        buttonBar.Children.Add(cancelButton);
        buttonBar.Children.Add(discardButton);
        buttonBar.Children.Add(saveButton);
        grid.Children.Add(buttonBar);

        dialog.Content = grid;

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close(SaveDiscardCancelResult.Cancel);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                dialog.Close(SaveDiscardCancelResult.Save);
                e.Handled = true;
            }
        };

        return await dialog.ShowDialog<SaveDiscardCancelResult>(owner);
    }
}
