namespace Historia.ScenarioLab.Views;

public partial class FileInfoPanel : UserControl
{
    public FileInfoPanel()
    {
        InitializeComponent();
    }

    public void SetScriptHeader(string shortDesc, string longDesc)
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

    public void SetDisabled()
    {
        RootGrid.IsEnabled = false;
        ShortTextBox.Text = string.Empty;
        LongTextBox.Text = string.Empty;
        ShortCounterText.Text = "0/72";
        ShortCounterText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ColorTextMuted"));
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
