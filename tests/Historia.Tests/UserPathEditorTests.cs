namespace Historia.Tests;

public sealed class UserPathEditorTests
{
    [WindowsFact]
    public void Appends_when_no_cmdr_entry()
    {
        var raw = @"C:\Tools;C:\Bin";
        var ourFolder = @"C:\Historia";
        var plan = UserPathEditor.PlanReplace(raw, ourFolder, _ => false);

        Assert.Equal(@"C:\Tools;C:\Bin;C:\Historia", plan.NewValue);
        Assert.Equal(@"C:\Historia", plan.Added);
        Assert.Empty(plan.Removed);
        Assert.False(plan.IsNoOp);
        Assert.False(plan.TooLong);
    }

    [WindowsFact]
    public void Replaces_old_cmdr_folder_in_place_and_keeps_neighbours_order()
    {
        var raw = @"C:\Tools;C:\OldCmdr;C:\Bin";
        var ourFolder = @"C:\NewCmdr";
        var plan = UserPathEditor.PlanReplace(raw, ourFolder, folder => string.Equals(folder, @"C:\OldCmdr", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"C:\Tools;C:\NewCmdr;C:\Bin", plan.NewValue);
        Assert.Equal(@"C:\NewCmdr", plan.Added);
        Assert.Equal([@"C:\OldCmdr"], plan.Removed);
        Assert.False(plan.IsNoOp);
    }

    [WindowsFact]
    public void Removes_second_cmdr_folder_and_duplicate_of_our_folder()
    {
        var raw = @"C:\Old1;C:\Tools;C:\Old2;C:\NewCmdr;C:\Bin";
        var ourFolder = @"C:\NewCmdr";
        var plan = UserPathEditor.PlanReplace(raw, ourFolder, folder => folder.Contains("Old", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"C:\NewCmdr;C:\Tools;C:\Bin", plan.NewValue);
        Assert.Equal(@"C:\NewCmdr", plan.Added);
        Assert.Equal([@"C:\Old1", @"C:\Old2", @"C:\NewCmdr"], plan.Removed);
        Assert.False(plan.IsNoOp);
    }

    [WindowsFact]
    public void NoOp_when_ours_is_the_only_one()
    {
        var raw = @"C:\Tools;C:\NewCmdr;C:\Bin";
        var ourFolder = @"C:\NewCmdr";
        var plan = UserPathEditor.PlanReplace(raw, ourFolder, folder => string.Equals(folder, @"C:\NewCmdr", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"C:\Tools;C:\NewCmdr;C:\Bin", plan.NewValue);
        Assert.Null(plan.Added);
        Assert.Empty(plan.Removed);
        Assert.True(plan.IsNoOp);
    }

    [WindowsFact]
    public void Folder_holding_cmdr_without_historia_is_left_in_place()
    {
        var raw = @"C:\StandaloneCmdr;C:\Bin";
        var ourFolder = @"C:\NewCmdr";

        // holdsCmdr returns false because folder holds historia-cmdr.exe but lacks Historia.exe
        var plan = UserPathEditor.PlanReplace(raw, ourFolder, _ => false);

        Assert.Equal(@"C:\StandaloneCmdr;C:\Bin;C:\NewCmdr", plan.NewValue);
        Assert.Equal(@"C:\NewCmdr", plan.Added);
        Assert.Empty(plan.Removed);
    }

    [WindowsFact]
    public void LocalAppData_variable_survives_verbatim()
    {
        var raw = @"%LOCALAPPDATA%\Foo;C:\OldCmdr;%USERPROFILE%\Bar";
        var ourFolder = @"C:\NewCmdr";
        var plan = UserPathEditor.PlanReplace(raw, ourFolder, folder => string.Equals(folder, @"C:\OldCmdr", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"%LOCALAPPDATA%\Foo;C:\NewCmdr;%USERPROFILE%\Bar", plan.NewValue);
        Assert.Equal(@"C:\NewCmdr", plan.Added);
        Assert.Equal([@"C:\OldCmdr"], plan.Removed);
    }

    [WindowsFact]
    public void Quoted_entry_and_trailing_slash_compare_equal()
    {
        var a = "\"C:\\Tools\\\"";
        var b = "C:\\Tools";

        Assert.True(UserPathEditor.SameFolder(a, b));
        Assert.True(UserPathEditor.SameFolder(b, a));
    }

    [WindowsFact]
    public void Empty_segments_dropped()
    {
        var raw = @";;C:\Tools;;C:\Bin;;;";
        var entries = UserPathEditor.Split(raw);

        Assert.Equal([@"C:\Tools", @"C:\Bin"], entries);

        var plan = UserPathEditor.PlanReplace(raw, @"C:\NewCmdr", _ => false);
        Assert.Equal(@"C:\Tools;C:\Bin;C:\NewCmdr", plan.NewValue);
    }

    [WindowsFact]
    public void Entry_with_invalid_path_characters_is_copied_never_matched()
    {
        var raw = @"C:\Tools;>invalid<|path;C:\Bin";
        var ourFolder = @"C:\NewCmdr";

        Assert.False(UserPathEditor.SameFolder(@">invalid<|path", ourFolder));

        var plan = UserPathEditor.PlanReplace(raw, ourFolder, _ => false);
        Assert.Equal(@"C:\Tools;>invalid<|path;C:\Bin;C:\NewCmdr", plan.NewValue);
    }

    [WindowsFact]
    public void Over_length_value_reports_TooLong()
    {
        var repeated = string.Join(';', Enumerable.Range(0, 1500).Select(i => $@"C:\VeryLongPathSegmentNumber{i:D4}\SubFolder"));
        var plan = UserPathEditor.PlanReplace(repeated, @"C:\NewCmdr", _ => false);

        Assert.True(plan.NewValue.Length > 32767);
        Assert.True(plan.TooLong);
    }

    [WindowsFact]
    public void Our_folder_written_as_a_variable_keeps_its_text_when_another_cmdr_entry_is_removed()
    {
        var raw = @"%LOCALAPPDATA%\HistoriaX;C:\Old";
        var ourFolder = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\HistoriaX\");
        var plan = UserPathEditor.PlanReplace(raw, ourFolder, folder => string.Equals(folder, @"C:\Old", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"%LOCALAPPDATA%\HistoriaX", plan.NewValue);
        Assert.Null(plan.Added);
        Assert.Equal([@"C:\Old"], plan.Removed);
        Assert.False(plan.IsNoOp);
    }

    [WindowsFact]
    public void Relative_and_unresolved_entries_are_copied_never_matched()
    {
        var raw = @".;bin;%HISTORIA_UNDEFINED_VAR%\x";
        var ourFolder = Environment.CurrentDirectory;

        Assert.False(UserPathEditor.SameFolder(".", Environment.CurrentDirectory));

        var plan = UserPathEditor.PlanReplace(raw, ourFolder, _ => true);

        Assert.Equal($@".;bin;%HISTORIA_UNDEFINED_VAR%\x;{ourFolder}", plan.NewValue);
        Assert.Equal(ourFolder, plan.Added);
        Assert.Empty(plan.Removed);
    }

    [WindowsFact]
    public void ExpandEntry_returns_null_for_relative_and_unquoted_full_path_for_quoted()
    {
        Assert.Null(UserPathEditor.ExpandEntry("."));
        Assert.Null(UserPathEditor.ExpandEntry("bin"));
        Assert.Null(UserPathEditor.ExpandEntry(@"%HISTORIA_UNDEFINED_VAR%\x"));
        Assert.Null(UserPathEditor.ExpandEntry(""));
        Assert.Null(UserPathEditor.ExpandEntry("   "));

        var quoted = "\"C:\\Tools\\Sub\"";
        Assert.Equal(@"C:\Tools\Sub", UserPathEditor.ExpandEntry(quoted));
    }
}
