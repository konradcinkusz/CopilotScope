using CopilotScope.AgentForge.Agents;
using CopilotScope.AgentForge.Clients;
using CopilotScope.AgentForge.Domain;
using CopilotScope.AgentForge.Profiling;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using Xunit;

namespace CopilotScope.Tests;

// Exercises the same sequence Program.cs's /provision and /chat handlers run — profile build →
// prompt build → cache → chat client — without hosting the app. This repo's existing tests
// (CollectorTests.cs, SeederTests.cs) test logic directly rather than through HTTP round-trips;
// AgentForge follows that same convention rather than introducing WebApplicationFactory here.
public class PersonaProvisioningFlowTests
{
    private sealed class FakeCollectorClient(SessionDetailDto session) : ICollectorClient
    {
        public Task<SessionDetailDto?> GetSessionDetailAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<SessionDetailDto?>(sessionId == session.Summary.Id ? session : null);
    }

    private sealed class StubChatClient : IPersonaChatClient
    {
        public string? LastSystemPrompt { get; private set; }

        public Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct)
        {
            LastSystemPrompt = systemPrompt;
            return Task.FromResult($"stubbed reply to: {userMessage}");
        }
    }

    [Fact]
    public async Task ProvisionThenChat_GroundsReplyOnPersonaTranscript()
    {
        var transcript = new List<TranscriptEntry>
        {
            new(DateTimeOffset.UtcNow, "claude", "help me refactor this loop", "Extract the body into a method.", 0)
        };
        var session = AgentForgeTestSupport.MakeSessionDetail("s-1", 77, transcript, new List<ToolStatDto>());
        var cohort = new PersonaCohort
        {
            PersonaId = "p-1",
            DisplayLabel = "Test Persona",
            ConsentGrantedBy = "alice",
            ConsentDate = new DateOnly(2026, 1, 1),
            SessionIds = new List<string> { "s-1" }
        };

        var profileBuilder = new PersonaProfileBuilder(new FakeCollectorClient(session));
        var promptBuilder = new PersonaPromptBuilder();
        var cache = new ProvisionedAgentCache();
        var chatClient = new StubChatClient();

        // /provision
        var profile = await profileBuilder.BuildAsync(cohort, CancellationToken.None);
        var systemPrompt = promptBuilder.Build(profile);
        cache.Set("p-1", new ProvisionedAgent(profile, systemPrompt, DateTimeOffset.UtcNow));

        // /chat
        Assert.True(cache.TryGet("p-1", out var provisioned));
        var reply = await chatClient.ChatAsync(provisioned.SystemPrompt, "how should I approach this bug?", CancellationToken.None);

        Assert.Equal("stubbed reply to: how should I approach this bug?", reply);
        Assert.Contains("Test Persona", chatClient.LastSystemPrompt);
        Assert.Contains("help me refactor this loop", chatClient.LastSystemPrompt);
    }

    [Fact]
    public void Chat_WithoutPriorProvisioning_FindsNothingInCache()
    {
        var cache = new ProvisionedAgentCache();

        Assert.False(cache.TryGet("never-provisioned", out _));
    }

    [Fact]
    public void Delete_RemovesProvisionedAgentSoChatWouldNoLongerFindIt()
    {
        var cache = new ProvisionedAgentCache();
        var profile = new PersonaProfile("p-1", "Test Persona", 1, 80, new List<string>(), new List<ExemplarTurn>());
        cache.Set("p-1", new ProvisionedAgent(profile, "system prompt", DateTimeOffset.UtcNow));

        var removed = cache.Remove("p-1");

        Assert.True(removed);
        Assert.False(cache.TryGet("p-1", out _));
    }
}
