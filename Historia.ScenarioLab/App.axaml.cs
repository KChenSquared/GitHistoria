namespace Historia.ScenarioLab;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!LabPaths.IsResolved)
            {
                var errorWindow = new Window
                {
                    Title = "ScenarioLab Error",
                    Width = 460,
                    CanResize = false,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                errorWindow.Bind(Window.BackgroundProperty, new DynamicResourceExtension("ColorChrome"));
                var stack = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
                var errorBlock = new TextBlock
                {
                    Text = "Could not locate ScenarioLab root directory.\nPlease verify the application is running within the repository.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14
                };
                errorBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ColorDanger"));
                stack.Children.Add(errorBlock);

                var okBtn = new Button { Content = "Exit", HorizontalAlignment = HorizontalAlignment.Right };
                okBtn.Classes.Add("primary");
                okBtn.Click += (_, _) => desktop.Shutdown(-1);
                stack.Children.Add(okBtn);
                errorWindow.Content = stack;
                desktop.MainWindow = errorWindow;
            }
            else
            {
                desktop.MainWindow = new MainWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
