namespace Historia.ScenarioLab.Views;

public partial class ScriptViewerPanel : UserControl
{
    public string? CurrentPath { get; private set; }
    public ScriptHeaderInfo CurrentHeader { get; private set; } = new(string.Empty, string.Empty);

    public ScriptViewerPanel()
    {
        InitializeComponent();
    }

    public void Load(string path)
    {
        if (!File.Exists(path))
            return;

        CurrentPath = path;
        var text = File.ReadAllText(path, Encoding.UTF8);
        Viewer.Text = text;
        CurrentHeader = ScriptHeader.Parse(text);
    }

    public void RetargetPath(string newPath)
    {
        CurrentPath = newPath;
    }

    public void Close()
    {
        CurrentPath = null;
        Viewer.Text = string.Empty;
        CurrentHeader = new(string.Empty, string.Empty);
    }
}
