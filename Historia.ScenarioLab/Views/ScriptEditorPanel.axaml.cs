namespace Historia.ScenarioLab.Views;

public partial class ScriptEditorPanel : UserControl
{
    public event Action<bool>? DirtyChanged;

    private readonly RegistryOptions _registryOptions;
    private readonly TextMate.Installation _installation;
    private string _loadedText = string.Empty;
    private bool _isDirty;
    private bool _isLoading;

    public TextEditor TextEditor => Editor;
    public string? CurrentPath { get; private set; }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty != value)
            {
                _isDirty = value;
                DirtyChanged?.Invoke(_isDirty);
            }
        }
    }

    public ScriptEditorPanel()
    {
        InitializeComponent();

        _registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        _installation = Editor.InstallTextMate(_registryOptions);

        Editor.TextChanged += (_, _) =>
        {
            if (_isLoading) return;
            IsDirty = Editor.Text != _loadedText;
        };

        KeyDown += async (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.S)
            {
                await SaveAsync();
                e.Handled = true;
            }
        };
    }

    public void Load(string path)
    {
        if (!File.Exists(path))
            return;

        _isLoading = true;
        try
        {
            CurrentPath = path;
            var text = File.ReadAllText(path, Encoding.UTF8);
            _loadedText = text;
            Editor.Text = text;
            UpdateGrammar(path);
            IsDirty = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    public void RetargetPath(string newPath)
    {
        CurrentPath = newPath;
        UpdateGrammar(newPath);
    }

    public void Close()
    {
        _isLoading = true;
        try
        {
            CurrentPath = null;
            _loadedText = string.Empty;
            Editor.Text = string.Empty;
            UpdateGrammar(string.Empty);
            IsDirty = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    public async Task SaveAsync()
    {
        if (string.IsNullOrEmpty(CurrentPath))
            return;

        var text = Editor.Text ?? string.Empty;
        if (CurrentPath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Replace("\r\n", "\n");
            if (Editor.Text != text)
            {
                var caret = Editor.CaretOffset;
                Editor.Text = text;
                Editor.CaretOffset = Math.Min(caret, text.Length);
            }
        }

        await File.WriteAllTextAsync(CurrentPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _loadedText = text;
        IsDirty = false;
    }

    private void UpdateGrammar(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            _installation.SetGrammar(null);
            return;
        }

        var lang = _registryOptions.GetLanguageByExtension(ext);
        if (lang is not null)
        {
            var scope = _registryOptions.GetScopeByLanguageId(lang.Id);
            _installation.SetGrammar(scope);
        }
        else
        {
            _installation.SetGrammar(null);
        }
    }
}
