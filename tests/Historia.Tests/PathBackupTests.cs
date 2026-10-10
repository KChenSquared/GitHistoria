namespace Historia.Tests;

[SupportedOSPlatform("windows")]
public sealed class PathBackupTests
{
    [Fact]
    public void Header_and_key_line_are_exact()
    {
        var reg = PathBackup.ToRegFile(@"C:\Tools", RegistryValueKind.String);
        var lines = reg.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        Assert.Equal("Windows Registry Editor Version 5.00", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.Equal("[HKEY_CURRENT_USER\\Environment]", lines[2]);
        Assert.Equal("\"Path\"=\"C:\\\\Tools\"", lines[3]);
    }

    [Fact]
    public void ExpandString_round_trips_with_special_characters_and_unicode()
    {
        var original = @"%LOCALAPPDATA%\Programs;C:\Path With Spaces\""Quoted\"";D:\Юникод_日本語_🚀";
        var reg = PathBackup.ToRegFile(original, RegistryValueKind.ExpandString);

        Assert.Contains("\"Path\"=hex(2):", reg);

        var (restored, kind) = PathBackup.FromRegFile(reg);
        Assert.Equal(original, restored);
        Assert.Equal(RegistryValueKind.ExpandString, kind);
    }

    [Fact]
    public void String_round_trips_with_backslashes_and_quotes()
    {
        var original = @"C:\Test\Path;C:\Another\""Nested\""\Path;%SYSTEMROOT%\System32";
        var reg = PathBackup.ToRegFile(original, RegistryValueKind.String);

        var (restored, kind) = PathBackup.FromRegFile(reg);
        Assert.Equal(original, restored);
        Assert.Equal(RegistryValueKind.String, kind);
    }

    [Fact]
    public void Long_value_wraps_hex2_lines()
    {
        var longValue = string.Join(';', Enumerable.Range(0, 50).Select(i => $@"C:\Folder{i}\SubFolder{i}"));
        var reg = PathBackup.ToRegFile(longValue, RegistryValueKind.ExpandString);

        Assert.Contains("\\\r\n  ", reg);

        var (restored, kind) = PathBackup.FromRegFile(reg);
        Assert.Equal(longValue, restored);
        Assert.Equal(RegistryValueKind.ExpandString, kind);
    }
}
