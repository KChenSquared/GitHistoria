namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class ControlProtocolTests
{
    public static IEnumerable<object[]> Verbs() =>
        Enum.GetValues<ControlVerb>().Select(v => new object[] { v });

    public static IEnumerable<object[]> Errors() =>
        Enum.GetValues<ControlError>().Select(e => new object[] { e });

    [Theory]
    [MemberData(nameof(Verbs))]
    public void Every_verb_round_trips_through_a_request(ControlVerb verb)
    {
        var request = new ControlRequest
        {
            Verb = verb,
            Cwd = @"C:\work",
            Repo = @"C:\work\repo",
            Rev = "HEAD~1",
            OtherRev = "main",
            Path = "src/a.cs",
            Line = 42,
            Staged = true,
            Focus = false,
            CommitMessage = "feat: x\n\nbody",
        };

        var json = ControlJson.Serialize(request);
        var back = ControlJson.DeserializeRequest(json);

        Assert.Equal(request, back);
        Assert.DoesNotContain('\n', json);
    }

    [Theory]
    [MemberData(nameof(Errors))]
    public void Every_error_round_trips_through_a_response(ControlError error)
    {
        var response = ControlResponse.Fail(error, "why");

        var back = ControlJson.DeserializeResponse(ControlJson.Serialize(response));

        Assert.Equal(response, back);
    }

    [Fact]
    public void Enums_serialize_as_kebab_case_strings()
    {
        var request = ControlJson.Serialize(new ControlRequest { Verb = ControlVerb.ProposeCommit });
        var response = ControlJson.Serialize(ControlResponse.Fail(ControlError.RevNotFound));
        var draft = ControlJson.Serialize(new ControlResponse { Ok = true, Draft = ControlDraftOutcome.Pending });

        Assert.Contains("\"verb\":\"propose-commit\"", request);
        Assert.Contains("\"error\":\"rev-not-found\"", response);
        Assert.Contains("\"draft\":\"pending\"", draft);
    }

    [Fact]
    public void Wire_names_are_camel_case_and_nulls_are_omitted()
    {
        var json = ControlJson.Serialize(new ControlResponse
        {
            Ok = true,
            OtherCommit = "abc",
            StagedPaths = ["a.txt", "b.txt"],
        });

        Assert.Equal("{\"ok\":true,\"otherCommit\":\"abc\",\"stagedPaths\":[\"a.txt\",\"b.txt\"]}", json);
    }

    [Fact]
    public void Propose_commit_reply_round_trips_with_staged_paths_and_draft()
    {
        var response = new ControlResponse
        {
            Ok = true,
            StagedPaths = ["a.txt", "b.txt"],
            Draft = ControlDraftOutcome.Filled,
        };

        var back = ControlJson.DeserializeResponse(ControlJson.Serialize(response))!;

        Assert.Equal(["a.txt", "b.txt"], back.StagedPaths);
        Assert.Equal(ControlDraftOutcome.Filled, back.Draft);
        Assert.Equal(ControlError.NothingStaged, ControlJson.DeserializeResponse(
            ControlJson.Serialize(ControlResponse.Fail(ControlError.NothingStaged)))!.Error);
    }

    [Fact]
    public void Selection_round_trips()
    {
        var response = new ControlResponse
        {
            Ok = true,
            Selection = new ControlSelection
            {
                Repo = @"C:\r",
                Commit = "abc",
                Diff = new ControlSelectionDiff { Source = "comparison", Path = "a.txt", Commit = "abc", OtherCommit = "def" },
            },
        };

        var back = ControlJson.DeserializeResponse(ControlJson.Serialize(response))!;

        Assert.Equal(response.Selection, back.Selection);
    }

    [Fact]
    public void Unknown_properties_are_ignored_and_focus_defaults_to_true()
    {
        var request = ControlJson.DeserializeRequest("{\"verb\":\"show\",\"rev\":\"HEAD\",\"future\":{\"x\":1}}")!;

        Assert.Equal(ControlVerb.Show, request.Verb);
        Assert.True(request.WantsFocus);
    }

    [Theory]
    [InlineData(true, null, 0)]
    [InlineData(false, ControlError.Usage, 64)]
    [InlineData(false, ControlError.Disabled, 2)]
    [InlineData(false, ControlError.Unreachable, 2)]
    [InlineData(false, ControlError.RepoNotFound, 1)]
    [InlineData(false, ControlError.RevNotFound, 1)]
    [InlineData(false, ControlError.PathInvalid, 1)]
    [InlineData(false, ControlError.NothingStaged, 1)]
    [InlineData(false, ControlError.Timeout, 1)]
    [InlineData(false, ControlError.Internal, 1)]
    public void Exit_codes_follow_the_error(bool ok, ControlError? error, int expected)
    {
        Assert.Equal(expected, ControlExitCode.For(new ControlResponse { Ok = ok, Error = error }));
    }

    [Fact]
    public void Verb_wire_names_parse_back_and_only_queries_never_launch()
    {
        foreach (var verb in Enum.GetValues<ControlVerb>())
        {
            Assert.True(ControlVerbs.TryParse(ControlVerbs.ToWire(verb), out var parsed));
            Assert.Equal(verb, parsed);
        }

        Assert.Equal("propose-commit", ControlVerbs.ToWire(ControlVerb.ProposeCommit));
        Assert.False(ControlVerbs.TryParse("Show", out _));
        Assert.False(ControlVerbs.MayLaunch(ControlVerb.Ping));
        Assert.False(ControlVerbs.MayLaunch(ControlVerb.Selection));
        Assert.True(ControlVerbs.MayLaunch(ControlVerb.ProposeCommit));
        Assert.True(ControlVerbs.MayLaunch(ControlVerb.Show));
    }
}
