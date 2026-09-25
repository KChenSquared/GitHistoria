namespace HistoriaApp.Tests;

public class UnsavedWorkCloseGateTests
{
    [Fact]
    public void No_unsaved_work_allows_close()
    {
        var gate = new UnsavedWorkCloseGate();
        var decision = gate.Evaluate(0);
        Assert.Equal(CloseDecision.Allow, decision);
    }

    [Fact]
    public void Close_while_prompt_open_is_cancelled_without_a_second_prompt()
    {
        var gate = new UnsavedWorkCloseGate();
        var first = gate.Evaluate(2);
        Assert.Equal(CloseDecision.CancelAndPrompt, first);

        var second = gate.Evaluate(2);
        Assert.Equal(CloseDecision.CancelOnly, second);

        var third = gate.Evaluate(1);
        Assert.Equal(CloseDecision.CancelOnly, third);
    }

    [Fact]
    public void Confirmed_prompt_allows_the_follow_up_close()
    {
        var gate = new UnsavedWorkCloseGate();
        var first = gate.Evaluate(1);
        Assert.Equal(CloseDecision.CancelAndPrompt, first);

        gate.Complete(proceed: true);

        var followUp = gate.Evaluate(1);
        Assert.Equal(CloseDecision.Allow, followUp);
    }

    [Fact]
    public void Cancelled_prompt_resets_so_the_next_close_prompts_again()
    {
        var gate = new UnsavedWorkCloseGate();
        var first = gate.Evaluate(1);
        Assert.Equal(CloseDecision.CancelAndPrompt, first);

        gate.Complete(proceed: false);

        var next = gate.Evaluate(1);
        Assert.Equal(CloseDecision.CancelAndPrompt, next);
    }
}
