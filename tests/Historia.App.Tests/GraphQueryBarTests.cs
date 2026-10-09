namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class GraphQueryBarTests
{
    [AvaloniaFact]
    public void Query_bar_contains_no_stashes_checkbox()
    {
        var graph = new CommitGraphControl();
        var checkBoxes = graph.QueryBarControl.GetLogicalDescendants().OfType<CheckBox>();
        Assert.DoesNotContain(checkBoxes, cb => Equals(cb.Content, "Stashes"));
    }
}
