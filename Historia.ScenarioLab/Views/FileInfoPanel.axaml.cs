namespace Historia.ScenarioLab.Views;

public partial class FileInfoPanel : UserControl
{
    public event Action<string, string>? HeaderEdited;

    private bool _isUpdating;

    public FileInfoPanel()
    {
        InitializeComponent();

        ShortTextBox.TextChanged += (_, _) =>
        {
            UpdateCounter();
            if (!_isUpdating)
            {
                HeaderEdited?.Invoke(ShortTextBox.Text ?? string.Empty, LongTextBox.Text ?? string.Empty);
            }
        };

        LongTextBox.TextChanged += (_, _) =>
        {
            if (!_isUpdating)
            {
                HeaderEdited?.Invoke(ShortTextBox.Text ?? string.Empty, LongTextBox.Text ?? string.Empty);
            }
        };
    }

    public void SetScriptHeader(string shortDesc, string longDesc)
    {
        _isUpdating = true;
        try
        {
            RootGrid.IsEnabled = true;
            if (ShortTextBox.Text != shortDesc)
            {
                ShortTextBox.Text = shortDesc;
            }
            if (LongTextBox.Text != longDesc)
            {
                LongTextBox.Text = longDesc;
            }
            UpdateCounter();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    public void SetDisabled()
    {
        _isUpdating = true;
        try
        {
            RootGrid.IsEnabled = false;
            ShortTextBox.Text = string.Empty;
            LongTextBox.Text = string.Empty;
            ShortCounterText.Text = "0/72";
            ShortCounterText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ColorTextMuted"));
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void UpdateCounter()
    {
        var len = ShortTextBox.Text?.Length ?? 0;
        ShortCounterText.Text = $"{len}/72";

        if (len > 72)
        {
            ShortCounterText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ColorDanger"));
        }
        else
        {
            ShortCounterText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ColorTextMuted"));
        }
    }
}
