namespace Historia.Tests;

[Trait("tier", "unit")]
public class AiIntegrationsPanelTests
{
    [Fact]
    public void Open_sets_state_and_raises_once()
    {
        var panel = new AiIntegrationsPanel();
        var raised = 0;
        panel.OpenChanged += (_, _) => raised++;

        panel.Open("abc");

        Assert.True(panel.IsOpen);
        Assert.Equal("abc", panel.FocusProfileId);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Open_when_already_open_raises_again_with_the_new_focus()
    {
        var panel = new AiIntegrationsPanel();
        panel.Open("a");
        var raised = 0;
        panel.OpenChanged += (_, _) => raised++;

        panel.Open("b");

        Assert.Equal("b", panel.FocusProfileId);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Close_clears_state_and_raises_only_when_it_was_open()
    {
        var panel = new AiIntegrationsPanel();
        var raised = 0;
        panel.OpenChanged += (_, _) => raised++;

        panel.Close();
        Assert.Equal(0, raised);

        panel.Open("a");
        panel.Close();

        Assert.False(panel.IsOpen);
        Assert.Null(panel.FocusProfileId);
        Assert.Equal(2, raised);
    }
}
