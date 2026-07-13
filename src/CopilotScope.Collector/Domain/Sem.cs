namespace CopilotScope.Collector.Domain;

/// <summary>
/// Signal names and attribute keys emitted by Copilot Chat in VS Code.
/// Source: VS Code docs "Monitor agent usage with OpenTelemetry" +
/// OTel GenAI Semantic Conventions. Legacy copilot_chat.* keys are kept
/// because they are dual-emitted with no sunset date.
/// </summary>
public static class Sem
{
    // Resource
    public const string ServiceName = "service.name";
    public const string SessionId = "session.id";

    // Span-level (gen_ai.*)
    public const string Operation = "gen_ai.operation.name";       // invoke_agent | chat | execute_tool | execute_hook
    public const string ConversationId = "gen_ai.conversation.id";
    public const string AgentName = "gen_ai.agent.name";
    public const string RequestModel = "gen_ai.request.model";
    public const string ResponseModel = "gen_ai.response.model";
    public const string InputTokens = "gen_ai.usage.input_tokens";
    public const string OutputTokens = "gen_ai.usage.output_tokens";
    public const string CacheReadTokens = "gen_ai.usage.cache_read.input_tokens";
    public const string CacheCreationTokens = "gen_ai.usage.cache_creation.input_tokens";
    public const string ToolName = "gen_ai.tool.name";
    public const string ErrorType = "error.type";
    public const string InputMessages = "gen_ai.input.messages";   // content capture only
    public const string OutputMessages = "gen_ai.output.messages"; // content capture only
    public const string Prompt = "gen_ai.prompt";                   // legacy content key
    public const string Completion = "gen_ai.completion";           // legacy content key

    // Span-level (copilot)
    public const string TimeToFirstToken = "copilot_chat.time_to_first_token"; // ms, on chat spans
    public const string TurnCount = "copilot_chat.turn_count";                 // on invoke_agent
    public const string GitRepository = "github.copilot.git.repository";
    public const string GitBranch = "github.copilot.git.branch";

    // Metrics
    public const string MTokenUsage = "gen_ai.client.token.usage";
    public const string MOperationDuration = "gen_ai.client.operation.duration";
    public const string MToolCallCount = "copilot_chat.tool.call.count";
    public const string MToolCallDuration = "copilot_chat.tool.call.duration";
    public const string MAgentDuration = "copilot_chat.agent.invocation.duration";
    public const string MTurnCount = "copilot_chat.agent.turn.count";
    public const string MSessionCount = "copilot_chat.session.count";
    public const string MTtft = "copilot_chat.time_to_first_token";
    public const string MEditAcceptance = "copilot_chat.edit.acceptance.count";
    public const string MChatEditOutcome = "copilot_chat.chat_edit.outcome.count";
    public const string MLinesOfCode = "copilot_chat.lines_of_code.count";
    public const string MSurvivalFourGram = "copilot_chat.edit.survival.four_gram";
    public const string MSurvivalNoRevert = "copilot_chat.edit.survival.no_revert";
    public const string MUserFeedback = "copilot_chat.user.feedback.count";
    public const string MUserAction = "copilot_chat.user.action.count";

    // Alternate namespaces: Copilot CLI (and recent VS Code builds) emit the
    // canonical github.copilot.* attribute/metric namespace; legacy copilot_chat.*
    // is dual-emitted by VS Code with no sunset date. Normalize() folds the
    // canonical names onto the legacy ones so the aggregation switch stays single.
    public const string TimeToFirstTokenGh = "github.copilot.time_to_first_token";
    public const string TimeToFirstTokenSrv = "gen_ai.server.time_to_first_token";
    public const string TurnCountGh = "github.copilot.turn_count";

    public static string Normalize(string name) =>
        name.StartsWith("github.copilot.", StringComparison.Ordinal)
            ? "copilot_chat." + name["github.copilot.".Length..]
            : name;

    // Events
    public const string ESessionStart = "copilot_chat.session.start";
    public const string EUserFeedback = "copilot_chat.user.feedback";
    public const string EEditFeedback = "copilot_chat.edit.feedback";
    public const string EAgentTurn = "copilot_chat.agent.turn";
    public const string EToolCall = "copilot_chat.tool.call";
}
