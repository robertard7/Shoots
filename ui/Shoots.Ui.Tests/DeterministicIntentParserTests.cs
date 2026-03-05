using Shoots.UI.Intents;
using Xunit;

namespace Shoots.UI.Tests;

public sealed class DeterministicIntentParserTests
{
    private readonly DeterministicIntentParser _parser = new();

    [Theory]
    [InlineData("start new project", IntentKind.CreateProject)]
    [InlineData("new project", IntentKind.CreateProject)]
    [InlineData("new project called foo", IntentKind.CreateProject)]
    [InlineData("new project named my app", IntentKind.CreateProject)]
    [InlineData("open project C:/dev/foo", IntentKind.OpenProject)]
    [InlineData("open project C:\\dev\\foo", IntentKind.OpenProject)]
    [InlineData("run demo", IntentKind.RunDemoPlan)]
    [InlineData("run demo plan", IntentKind.RunDemoPlan)]
    [InlineData("build plan plans/demo.mmd", IntentKind.BuildFromPlanFile)]
    [InlineData("build plan plans\\demo.mmd", IntentKind.BuildFromPlanFile)]
    [InlineData("add note remember this", IntentKind.AddNote)]
    [InlineData("Add Note TODO item", IntentKind.AddNote)]
    [InlineData("   start   new   project   ", IntentKind.CreateProject)]
    [InlineData("OPEN PROJECT /tmp/x", IntentKind.OpenProject)]
    [InlineData("RUN DEMO", IntentKind.RunDemoPlan)]
    [InlineData("new project called Foo Bar", IntentKind.CreateProject)]
    [InlineData("start a new project", IntentKind.CreateProject)]
    [InlineData("create project called alpha", IntentKind.CreateProject)]
    [InlineData("make a project in C:\\tmp\\foo", IntentKind.CreateProject)]
    [InlineData("create new workspace", IntentKind.CreateProject)]
    [InlineData("build plan ./plans/demo.mmd", IntentKind.BuildFromPlanFile)]
    [InlineData("open project ./workspace", IntentKind.OpenProject)]
    [InlineData("add note capture requirements", IntentKind.AddNote)]
    [InlineData("totally unknown phrase", IntentKind.Unknown)]
    public void Parse_maps_input_to_expected_intent_kind(string input, IntentKind expectedKind)
    {
        var result = _parser.Parse(input);

        Assert.Equal(expectedKind, result.Kind);
        Assert.Equal(input, result.RawUserText);
        Assert.NotEqual(System.Guid.Empty, result.IntentId);
    }

    [Fact]
    public void Parse_extracts_create_project_name_argument()
    {
        var result = _parser.Parse("new project called Alpha");

        Assert.Equal(IntentKind.CreateProject, result.Kind);
        Assert.Equal("alpha", result.Args["name"]);
    }

    [Fact]
    public void Parse_is_deterministic_for_same_input_shape()
    {
        var one = _parser.Parse("run demo");
        var two = _parser.Parse("run demo");

        Assert.Equal(one.Kind, two.Kind);
        Assert.Equal(one.NormalizedText, two.NormalizedText);
        Assert.Equal(one.Diagnostics, two.Diagnostics);
    }
}
