using CopilotScope.AgentForge.Agents;
using CopilotScope.AgentForge.Domain;
using Xunit;

namespace CopilotScope.Tests;

public class PersonaPromptBuilderTests
{
    [Fact]
    public void Build_SubstitutesAllPlaceholders()
    {
        var profile = new PersonaProfile(
            "p-1", "Ada Lovelace", 3, 91.5,
            new List<string> { "read_file", "edit_file" },
            new List<ExemplarTurn>
            {
                new("How do I add retries?", "Wrap the call in a retry policy.", 90),
                new("Why is this test flaky?", "It depends on wall-clock time; inject a clock.", 93)
            });

        var prompt = new PersonaPromptBuilder().Build(profile);

        Assert.Contains("Ada Lovelace", prompt);
        Assert.Contains("91.5", prompt);
        Assert.Contains("read_file", prompt);
        Assert.Contains("How do I add retries?", prompt);
        Assert.Contains("Wrap the call in a retry policy.", prompt);
        Assert.Contains("Why is this test flaky?", prompt);
        Assert.DoesNotContain("{{", prompt);
    }

    [Fact]
    public void Build_WithNoExemplars_StillProducesAPrompt()
    {
        var profile = new PersonaProfile("p-2", "Nobody Yet", 0, 0, new List<string>(), new List<ExemplarTurn>());

        var prompt = new PersonaPromptBuilder().Build(profile);

        Assert.Contains("Nobody Yet", prompt);
        Assert.DoesNotContain("{{", prompt);
    }
}
