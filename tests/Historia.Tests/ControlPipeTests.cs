namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class ControlPipeTests
{
    private static string NewPipeName() => "Historia.Test." + Guid.NewGuid().ToString("N")[..8];

    private static NamedPipeServerStream NewServer(string name, PipeDirection direction = PipeDirection.InOut) =>
        new(name, direction, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static async Task<ControlRead> ReadFromTextAsync(string text)
    {
        using var reader = new StringReader(text);
        return await ControlPipe.ReadAsync(reader);
    }

    [Fact]
    public async Task A_request_and_its_response_cross_the_pipe()
    {
        var name = NewPipeName();
        using var server = NewServer(name);
        var request = new ControlRequest { Verb = ControlVerb.Show, Rev = "HEAD", Repo = @"C:\r" };

        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = ControlPipe.CreateReader(server);
            using var writer = ControlPipe.CreateWriter(server);
            var read = await ControlPipe.ReadAsync(reader);
            Assert.Equal(ControlReadKind.Request, read.Kind);
            await ControlPipe.WriteResponseAsync(writer, new ControlResponse { Ok = true, Commit = read.Request!.Rev });
        });

        var connected = await ControlPipe.ConnectClientAsync(TimeSpan.FromSeconds(5), pipeName: name);
        Assert.Equal(ControlConnectOutcome.Connected, connected.Outcome);
        using var client = connected.Stream!;
        using var clientWriter = ControlPipe.CreateWriter(client);
        using var clientReader = ControlPipe.CreateReader(client);

        await ControlPipe.WriteRequestAsync(clientWriter, request);
        var response = await ControlPipe.ReadResponseAsync(clientReader);
        await serving;

        Assert.True(response!.Ok);
        Assert.Equal("HEAD", response.Commit);
    }

    [Fact]
    public async Task A_first_line_that_is_not_the_header_is_legacy_with_every_line_preserved()
    {
        var read = await ReadFromTextAsync("C:\\repo one\nD:\\repo two\n");

        Assert.Equal(ControlReadKind.Legacy, read.Kind);
        Assert.Equal([@"C:\repo one", @"D:\repo two"], read.Lines);
    }

    [Fact]
    public async Task A_connection_with_no_lines_is_empty()
    {
        Assert.Equal(ControlReadKind.Empty, (await ReadFromTextAsync("")).Kind);
    }

    [Fact]
    public async Task The_header_followed_by_json_is_a_request()
    {
        var json = ControlJson.Serialize(new ControlRequest { Verb = ControlVerb.Ping });

        var read = await ReadFromTextAsync(ControlPipe.ProtocolHeader + "\n" + json + "\n");

        Assert.Equal(ControlReadKind.Request, read.Kind);
        Assert.Equal(ControlVerb.Ping, read.Request!.Verb);
    }

    [Fact]
    public async Task A_header_with_no_request_or_bad_json_is_invalid_usage()
    {
        var missing = await ReadFromTextAsync(ControlPipe.ProtocolHeader + "\n");
        var bad = await ReadFromTextAsync(ControlPipe.ProtocolHeader + "\n{not json\n");

        Assert.Equal(ControlReadKind.Invalid, missing.Kind);
        Assert.Equal(ControlReadKind.Invalid, bad.Kind);
        Assert.Equal(ControlError.Usage, bad.Failure!.Error);
    }

    [Fact]
    public async Task An_oversize_request_line_is_usage()
    {
        var huge = new string('a', ControlPipe.MaxLineChars + 10);

        var read = await ReadFromTextAsync(ControlPipe.ProtocolHeader + "\n" + huge + "\n");

        Assert.Equal(ControlReadKind.Invalid, read.Kind);
        Assert.Equal(ControlError.Usage, read.Failure!.Error);
    }

    [Fact]
    public async Task An_oversize_first_line_is_usage()
    {
        var read = await ReadFromTextAsync(new string('a', ControlPipe.MaxLineChars + 10) + "\n");

        Assert.Equal(ControlReadKind.Invalid, read.Kind);
    }

    [Fact]
    public async Task A_line_at_the_cap_is_accepted()
    {
        var line = new string('a', ControlPipe.MaxLineChars);

        var read = await ReadFromTextAsync(line + "\n");

        Assert.Equal(ControlReadKind.Legacy, read.Kind);
        Assert.Equal(ControlPipe.MaxLineChars, read.Lines![0].Length);
    }

    [Fact]
    public async Task Crlf_line_endings_are_tolerated()
    {
        var json = ControlJson.Serialize(new ControlRequest { Verb = ControlVerb.Ping });

        var read = await ReadFromTextAsync(ControlPipe.ProtocolHeader + "\r\n" + json + "\r\n");

        Assert.Equal(ControlReadKind.Request, read.Kind);
    }

    [Fact]
    public async Task A_reply_that_is_not_json_reads_as_null()
    {
        using var reader = new StringReader("nonsense\n");

        Assert.Null(await ControlPipe.ReadResponseAsync(reader));
        Assert.Null(await ControlPipe.ReadResponseAsync(new StringReader("")));
    }

    [Fact]
    public void The_pipe_name_honours_the_environment_override()
    {
        var original = Environment.GetEnvironmentVariable(ControlPipe.PipeNameVariable);
        try
        {
            Environment.SetEnvironmentVariable(ControlPipe.PipeNameVariable, null);
            Assert.Equal(ControlPipe.DefaultPipeName, ControlPipe.PipeName);

            Environment.SetEnvironmentVariable(ControlPipe.PipeNameVariable, "Historia.Custom.Pipe");
            Assert.Equal("Historia.Custom.Pipe", ControlPipe.PipeName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ControlPipe.PipeNameVariable, original);
        }
    }

    [Fact]
    public async Task Nothing_listening_is_unreachable()
    {
        var result = await ControlPipe.ConnectClientAsync(TimeSpan.FromMilliseconds(200), pipeName: NewPipeName());

        Assert.Equal(ControlConnectOutcome.Unreachable, result.Outcome);
        Assert.Null(result.Stream);
    }

    [Fact]
    public async Task An_in_only_server_is_reported_incompatible()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var name = NewPipeName();
        using var server = NewServer(name, PipeDirection.In);

        var result = await ControlPipe.ConnectClientAsync(TimeSpan.FromSeconds(2), pipeName: name);

        Assert.Equal(ControlConnectOutcome.Incompatible, result.Outcome);
        Assert.Null(result.Stream);
    }
}
