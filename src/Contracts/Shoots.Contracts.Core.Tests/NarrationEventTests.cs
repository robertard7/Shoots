using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shoots.Contracts.Core.AI.Narration;
using Xunit;

namespace Shoots.Contracts.Core.Tests;

public sealed class NarrationEventTests
{
    [Fact]
    public void Data_keys_are_sorted_deterministically()
    {
        var evt = new NarrationEvent(
            "plan",
            "plan.materialize.start",
            "Materializing plan",
            new Dictionary<string, string>
            {
                ["z"] = "2",
                ["a"] = "1"
            });

        var keys = new List<string>(evt.Data.Keys);

        Assert.Equal(new[] { "a", "z" }, keys);
    }

    [Fact]
    public void Serialization_matches_golden_text()
    {
        var evt = new NarrationEvent(
            "execute",
            "execute.step.begin",
            "Running step",
            new Dictionary<string, string>
            {
                ["stepId"] = "abc",
                ["toolId"] = "linux.noop.v1"
            });

        var payload = new
        {
            phase = evt.Phase,
            code = evt.Code,
            message = evt.Message,
            data = evt.Data
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        const string golden = "{\"phase\":\"execute\",\"code\":\"execute.step.begin\",\"message\":\"Running step\",\"data\":{\"stepId\":\"abc\",\"toolId\":\"linux.noop.v1\"}}";

        Assert.Equal(golden, json);
    }

    [Fact]
    public void Codebook_rejects_unknown_phase()
    {
        var evt = new NarrationEvent("unknown", "plan.read", "Reading plan");

        var valid = NarrationCodebook.TryValidate(evt, out var error);

        Assert.False(valid);
        Assert.Equal("narration.phase.unknown", error);
    }

    [Fact]
    public void Codebook_requires_error_code_for_error_severity()
    {
        var evt = new NarrationEvent("execute", "error", "Failure");

        var valid = NarrationCodebook.TryValidate(evt, out var error);

        Assert.False(valid);
        Assert.Equal("narration.errorcode.missing", error);
    }
}
