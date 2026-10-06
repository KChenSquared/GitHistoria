namespace Historia.Tests;

[Trait("tier", "unit")]
public class GeminiModelSelectorTests
{
    private static string? Pick(string? excluding, params string[] ids) =>
        GeminiModelSelector.PickReplacement(ids, excluding);

    [Fact]
    public void Stable_flash_beats_a_newer_preview()
    {
        Assert.Equal("gemini-2.5-flash",
            Pick(null, "gemini-2.0-flash", "gemini-2.5-flash", "gemini-2.5-pro", "gemini-3-flash-preview"));
    }

    [Fact]
    public void Preview_flash_is_chosen_when_there_is_no_stable_flash()
    {
        Assert.Equal("gemini-3-flash-preview", Pick(null, "gemini-3-flash-preview", "gemini-3-pro-preview"));
    }

    [Fact]
    public void Any_stable_model_beats_a_preview_when_there_is_no_flash()
    {
        Assert.Equal("gemini-2.5-pro", Pick(null, "gemini-2.5-pro", "gemini-3-pro-preview"));
    }

    [Fact]
    public void Flash_lite_is_chosen_over_pro_when_it_is_the_only_flash()
    {
        Assert.Equal("gemini-2.5-flash-lite", Pick(null, "gemini-2.5-pro", "gemini-2.5-flash-lite"));
    }

    [Fact]
    public void Stable_flash_lite_beats_preview_flash_lite()
    {
        Assert.Equal("gemini-2.0-flash-lite", Pick(null, "gemini-3-flash-lite-preview", "gemini-2.0-flash-lite"));
    }

    [Fact]
    public void Plain_alias_beats_a_versioned_suffix()
    {
        Assert.Equal("gemini-2.5-flash", Pick(null, "gemini-2.5-flash-001", "gemini-2.5-flash"));
    }

    [Fact]
    public void Higher_version_wins_within_a_tier()
    {
        Assert.Equal("gemini-2.5-flash", Pick(null, "gemini-2.0-flash", "gemini-2.5-flash", "gemini-1.5-flash"));
    }

    [Fact]
    public void Specialised_models_are_never_chosen_while_another_exists()
    {
        Assert.Equal("gemini-2.0-flash",
            Pick(null, "gemini-2.5-flash-preview-tts", "gemini-2.5-flash-image", "gemini-2.0-flash"));
    }

    [Fact]
    public void Specialised_models_are_the_last_resort()
    {
        Assert.Equal("gemini-2.5-flash-preview-tts", Pick(null, "gemini-2.5-flash-preview-tts"));
    }

    [Fact]
    public void An_unversioned_alias_ranks_below_a_numbered_stable_flash()
    {
        Assert.Equal("gemini-2.0-flash", Pick(null, "gemini-flash-latest", "gemini-2.0-flash"));
    }

    [Fact]
    public void An_unversioned_alias_is_still_chosen_when_alone_in_its_tier()
    {
        Assert.Equal("gemini-flash-latest", Pick(null, "gemini-flash-latest", "gemini-2.5-pro"));
    }

    [Fact]
    public void The_excluded_model_is_never_returned()
    {
        Assert.Equal("gemini-2.0-flash", Pick("gemini-2.5-flash", "gemini-2.5-flash", "gemini-2.0-flash"));
        Assert.Equal("gemini-2.0-flash", Pick("models/gemini-2.5-flash", "models/gemini-2.5-flash", "gemini-2.0-flash"));
    }

    [Fact]
    public void Excluding_the_only_model_yields_null()
    {
        Assert.Null(Pick("gemini-2.5-flash", "gemini-2.5-flash"));
    }

    [Fact]
    public void An_empty_list_yields_null()
    {
        Assert.Null(GeminiModelSelector.PickReplacement([], null));
    }

    [Fact]
    public void The_models_prefix_is_stripped_from_the_result()
    {
        Assert.Equal("gemini-2.5-flash", Pick(null, "models/gemini-2.5-flash"));
    }

    [Fact]
    public void Ranking_does_not_depend_on_list_order()
    {
        string[] ids = ["gemini-2.0-flash", "gemini-2.5-flash", "gemini-3-flash-preview", "gemini-2.5-pro"];
        Assert.Equal(Pick(null, ids), Pick(null, ids.Reverse().ToArray()));
    }
}
