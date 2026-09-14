namespace Historia.ScenarioLab.Views;

public partial class OutputPanel : UserControl
{
    private readonly ConcurrentQueue<string> _pendingLines = new();
    private readonly DispatcherTimer _batchTimer;

    public OutputPanel()
    {
        InitializeComponent();

        ClearButton.Click += (_, _) => Clear();

        _batchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _batchTimer.Tick += (_, _) => FlushPendingLines();
        _batchTimer.Start();
    }

    public void Clear()
    {
        while (_pendingLines.TryDequeue(out _)) { }
        OutputEditor.Text = string.Empty;
    }

    public void AppendLine(string line)
    {
        _pendingLines.Enqueue(line + "\n");
    }

    public void Flush()
    {
        FlushPendingLines();
    }

    private void FlushPendingLines()
    {
        if (_pendingLines.IsEmpty)
            return;

        var sb = new StringBuilder();
        while (_pendingLines.TryDequeue(out var line))
        {
            sb.Append(line);
        }

        var text = sb.ToString();
        if (string.IsNullOrEmpty(text))
            return;

        OutputEditor.Document.Insert(OutputEditor.Document.TextLength, text);
        OutputEditor.CaretOffset = OutputEditor.Document.TextLength;
        OutputEditor.ScrollToEnd();
    }
}
