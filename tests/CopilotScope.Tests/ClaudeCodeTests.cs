using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Claude Code and Cowork ingest. What makes these surfaces different from every other
/// emitter: a default install exports metrics and log events under the claude_code.*
/// namespace and no spans at all, and it names its session in a record attribute rather
/// than a resource attribute. Cowork narrows that further to log events only.
/// </summary>
public class ClaudeCodeIngestTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, AttrValue> ClaudeResource(string service = "claude-code") =>
        new() { ["service.name"] = AttrValue.Str(service) };

    private static OtlpLogEvent Event(string name, string sessionId, string? promptId = null,
        double secondsIn = 0, string service = "claude-code", params (string Key, AttrValue Value)[] attrs)
    {
        var log = new OtlpLogEvent
        {
            EventName = name,
            Time = T0.AddSeconds(secondsIn),
            Resource = ClaudeResource(service),
            Attributes = new Dictionary<string, AttrValue> { ["session.id"] = AttrValue.Str(sessionId) }
        };
        if (promptId is not null) log.Attributes["prompt.id"] = AttrValue.Str(promptId);
        foreach (var (key, value) in attrs) log.Attributes[key] = value;
        return log;
    }

    private static OtlpMetricPoint Metric(string name, string sessionId, double value,
        params (string Key, AttrValue Value)[] attrs)
    {
        var point = new OtlpMetricPoint
        {
            MetricName = name,
            Kind = MetricKind.Sum,
            Time = T0,
            Value = value,
            Count = 1,
            Resource = ClaudeResource(),
            Attributes = new Dictionary<string, AttrValue> { ["session.id"] = AttrValue.Str(sessionId) }
        };
        foreach (var (key, attr) in attrs) point.Attributes[key] = attr;
        return point;
    }

    /// <summary>One realistic turn: prompt, two API calls, a tool call and an accepted edit.</summary>
    private static OtlpBatch DefaultInstallBatch(string sessionId, string promptId)
    {
        var batch = new OtlpBatch();
        batch.Logs.Add(Event("claude_code.user_prompt", sessionId, promptId, 0,
            attrs: [("prompt_length", AttrValue.Int(42))]));
        batch.Logs.Add(Event("claude_code.api_request", sessionId, promptId, 1,
            attrs: [
                ("model", AttrValue.Str("claude-opus-5")),
                ("input_tokens", AttrValue.Int(1200)),
                ("output_tokens", AttrValue.Int(300)),
                ("cache_read_tokens", AttrValue.Int(8000)),
                ("cache_creation_tokens", AttrValue.Int(500)),
                ("duration_ms", AttrValue.Dbl(2400))
            ]));
        batch.Logs.Add(Event("claude_code.tool_decision", sessionId, promptId, 2,
            attrs: [("tool_name", AttrValue.Str("Edit")), ("decision", AttrValue.Str("accept"))]));
        batch.Logs.Add(Event("claude_code.tool_result", sessionId, promptId, 3,
            attrs: [
                ("tool_name", AttrValue.Str("Edit")),
                ("success", AttrValue.Str("true")),
                ("duration_ms", AttrValue.Dbl(85))
            ]));
        batch.Logs.Add(Event("claude_code.api_request", sessionId, promptId, 4,
            attrs: [
                ("model", AttrValue.Str("claude-opus-5")),
                ("input_tokens", AttrValue.Int(400)),
                ("output_tokens", AttrValue.Int(150)),
                ("duration_ms", AttrValue.Dbl(900))
            ]));
        batch.Metrics.Add(Metric("claude_code.lines_of_code.count", sessionId, 24,
            ("type", AttrValue.Str("added"))));
        batch.Metrics.Add(Metric("claude_code.lines_of_code.count", sessionId, 6,
            ("type", AttrValue.Str("removed"))));
        return batch;
    }

    [Fact]
    public void DefaultInstallProducesAFullSessionFromEventsAlone()
    {
        var store = new SessionStore();

        var touched = store.Ingest(DefaultInstallBatch("cc-session-1", "prompt-1"));

        Assert.Contains("cc-session-1", touched);
        var session = Assert.Single(store.All);
        Assert.Equal("cc-session-1", session.Id);
        Assert.Equal(EmitterKind.ClaudeCode, session.EmitterKind);

        Assert.Equal(2, session.ChatCalls);
        Assert.Equal(0, session.ChatErrors);
        Assert.Equal(1600, session.InputTokens);
        Assert.Equal(450, session.OutputTokens);
        Assert.Equal(8000, session.CacheReadTokens);
        Assert.Equal(500, session.CacheCreationTokens);
        Assert.Equal(1, session.ToolCalls);
        Assert.Equal(0, session.ToolErrors);
        Assert.Equal(1, session.EditsAccepted);
        Assert.Equal(0, session.EditsRejected);
        Assert.Equal(24d, session.LinesAdded);
        Assert.Equal(6d, session.LinesRemoved);
        Assert.Equal(1, session.Turns);
        Assert.Equal(2, session.ModelUsage["claude-opus-5"].Calls);
    }

    [Fact]
    public void SessionIdAttributeKeepsConcurrentSessionsApart()
    {
        // Claude carries session.id per record, not on the resource. Without reading it,
        // every session from one machine would share the "svc:claude-code" fingerprint.
        var store = new SessionStore();

        store.Ingest(DefaultInstallBatch("cc-a", "prompt-a"));
        store.Ingest(DefaultInstallBatch("cc-b", "prompt-b"));

        Assert.Equal(2, store.All.Count);
        Assert.Equal(2, store.Get("cc-a")!.ChatCalls);
        Assert.Equal(2, store.Get("cc-b")!.ChatCalls);
    }

    [Fact]
    public void TokenAndEditMetricsDoNotDoubleCountTheEventsTheyMirror()
    {
        // claude_code.token.usage and claude_code.code_edit_tool.decision report the same
        // calls and decisions as the api_request / tool_decision events but carry no id to
        // de-duplicate against, so the events win and the metrics must be dropped.
        var store = new SessionStore();
        var batch = DefaultInstallBatch("cc-dedupe", "prompt-1");
        batch.Metrics.Add(Metric("claude_code.token.usage", "cc-dedupe", 1600,
            ("type", AttrValue.Str("input")), ("model", AttrValue.Str("claude-opus-5"))));
        batch.Metrics.Add(Metric("claude_code.token.usage", "cc-dedupe", 450,
            ("type", AttrValue.Str("output")), ("model", AttrValue.Str("claude-opus-5"))));
        batch.Metrics.Add(Metric("claude_code.code_edit_tool.decision", "cc-dedupe", 1,
            ("tool_name", AttrValue.Str("Edit")), ("decision", AttrValue.Str("accept"))));

        store.Ingest(batch);

        var session = store.Get("cc-dedupe")!;
        Assert.Equal(1600, session.InputTokens);
        Assert.Equal(450, session.OutputTokens);
        Assert.Equal(1, session.EditsAccepted);
    }

    [Fact]
    public void ClaudeMetricsDoNotLeakIntoCopilotRoutingViaTheNormalizedAlias()
    {
        // Sem.Normalize folds claude_code.* onto copilot_chat.*, so an unhandled Claude
        // metric would otherwise be read as a Copilot one under a name it never meant.
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Metrics.Add(Metric("claude_code.session.count", "cc-alias", 1));
        batch.Metrics.Add(Metric("claude_code.cost.usage", "cc-alias", 0.42));
        batch.Metrics.Add(Metric("claude_code.active_time.total", "cc-alias", 95));

        store.Ingest(batch);

        var session = store.Get("cc-alias")!;
        Assert.Equal(0d, session.LinesAdded);
        Assert.Equal(0, session.EditsAccepted);
        Assert.Equal(0, session.ChatCalls);
    }

    [Fact]
    public void BareEventNamesWithoutTheClaudeCodePrefixAreUnderstood()
    {
        // Depending on exporter version the name arrives as the record's own event name
        // ("claude_code.api_request") or as a bare event.name attribute ("api_request").
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Logs.Add(Event("api_request", "cc-bare", "prompt-1", 0,
            attrs: [("model", AttrValue.Str("claude-opus-5")), ("input_tokens", AttrValue.Int(10))]));

        store.Ingest(batch);

        var session = store.Get("cc-bare")!;
        Assert.Equal(1, session.ChatCalls);
        Assert.Equal(10, session.InputTokens);
    }

    [Fact]
    public void NumericAttributesArriveAsStringsFromSomeJsonExporters()
    {
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Logs.Add(Event("claude_code.api_request", "cc-strings", "prompt-1", 0,
            attrs: [
                ("model", AttrValue.Str("claude-opus-5")),
                ("input_tokens", AttrValue.Str("2048")),
                ("duration_ms", AttrValue.Str("1500.5"))
            ]));

        store.Ingest(batch);

        var session = store.Get("cc-strings")!;
        Assert.Equal(2048, session.InputTokens);
        Assert.Equal(1500.5, Assert.Single(session.ChatDurationMs));
    }

    [Fact]
    public void ApiErrorsCountAsAttemptedCallsAndAreTypedByStatus()
    {
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Logs.Add(Event("claude_code.api_error", "cc-err", "prompt-1", 0,
            attrs: [
                ("model", AttrValue.Str("claude-opus-5")),
                ("status_code", AttrValue.Int(529)),
                ("duration_ms", AttrValue.Dbl(300))
            ]));
        batch.Logs.Add(Event("claude_code.tool_result", "cc-err", "prompt-1", 1,
            attrs: [
                ("tool_name", AttrValue.Str("Bash")),
                ("success", AttrValue.Str("false")),
                ("error_type", AttrValue.Str("ShellError")),
                ("duration_ms", AttrValue.Dbl(50))
            ]));

        store.Ingest(batch);

        var session = store.Get("cc-err")!;
        Assert.Equal(1, session.ChatCalls);
        Assert.Equal(1, session.ChatErrors);
        Assert.Equal(1, session.ToolCalls);
        Assert.Equal(1, session.ToolErrors);
        Assert.Equal(1, session.ErrorTypes["http_529"]);
        Assert.Equal(1, session.ErrorTypes["ShellError"]);
    }

    [Fact]
    public void OnlyCodeEditingToolsFeedEditAcceptance()
    {
        // Claude asks permission for every tool; a Bash approval is not an accepted edit.
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Logs.Add(Event("claude_code.tool_decision", "cc-dec", "p", 0,
            attrs: [("tool_name", AttrValue.Str("Bash")), ("decision", AttrValue.Str("accept"))]));
        batch.Logs.Add(Event("claude_code.tool_decision", "cc-dec", "p", 1,
            attrs: [("tool_name", AttrValue.Str("Write")), ("decision", AttrValue.Str("accept"))]));
        batch.Logs.Add(Event("claude_code.tool_decision", "cc-dec", "p", 2,
            attrs: [("tool_name", AttrValue.Str("Edit")), ("decision", AttrValue.Str("reject"))]));

        store.Ingest(batch);

        var session = store.Get("cc-dec")!;
        Assert.Equal(1, session.EditsAccepted);
        Assert.Equal(1, session.EditsRejected);
    }

    [Fact]
    public void PromptIdGroupsTurnsWhenThereAreNoSpansToGroupThem()
    {
        var store = new SessionStore();
        var batch = new OtlpBatch();
        foreach (var (promptId, offset) in new[] { ("p-1", 0), ("p-2", 10) })
        {
            batch.Logs.Add(Event("claude_code.user_prompt", "cc-turns", promptId, offset));
            batch.Logs.Add(Event("claude_code.api_request", "cc-turns", promptId, offset + 1,
                attrs: [("model", AttrValue.Str("claude-opus-5")), ("output_tokens", AttrValue.Int(100))]));
        }

        store.Ingest(batch);

        var session = store.Get("cc-turns")!;
        Assert.Equal(2, session.Turns);
        Assert.Equal(2, session.TurnList.Count);
        Assert.All(session.TurnList, t => Assert.Equal(1, t.ChatCalls));
        Assert.Equal(100, session.TurnList[0].OutputTokens);
    }

    [Fact]
    public void CapturedContentBecomesTranscriptEntries()
    {
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Logs.Add(Event("claude_code.user_prompt", "cc-content", "p-1", 0,
            attrs: [("prompt", AttrValue.Str("why is the invoices endpoint slow"))]));
        batch.Logs.Add(Event("claude_code.assistant_response", "cc-content", "p-1", 2,
            attrs: [
                ("response", AttrValue.Str("It issues one query per row.")),
                ("model", AttrValue.Str("claude-opus-5"))
            ]));

        store.Ingest(batch);

        var transcript = store.Get("cc-content")!.Transcript;
        Assert.Equal(2, transcript.Count);
        Assert.Equal("why is the invoices endpoint slow", transcript[0].Prompt);
        Assert.Null(transcript[0].Response);
        Assert.Equal("It issues one query per row.", transcript[1].Response);
        Assert.Equal("claude-opus-5", transcript[1].Model);
        Assert.Equal(transcript[0].Turn, transcript[1].Turn);
    }

    [Fact]
    public void CoworkIsRecognisedFromLogEventsAlone()
    {
        // Cowork (the agent surface in the Claude desktop app) exports events only —
        // no metrics, no traces — and is configured from the app's own settings UI.
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Logs.Add(Event("claude_code.user_prompt", "cw-1", "p-1", 0, service: "claude-cowork"));
        batch.Logs.Add(Event("claude_code.api_request", "cw-1", "p-1", 1, service: "claude-cowork",
            attrs: [("model", AttrValue.Str("claude-opus-5")), ("input_tokens", AttrValue.Int(900))]));

        store.Ingest(batch);

        var session = store.Get("cw-1")!;
        Assert.Equal(EmitterKind.Cowork, session.EmitterKind);
        Assert.Equal(1, session.ChatCalls);
        Assert.Equal(900, session.InputTokens);
        Assert.Equal(1, session.Turns);
    }

    [Fact]
    public void BetaTraceSpansAddTimeToFirstTokenWithoutRecountingTheCall()
    {
        // claude_code.llm_request mirrors the api_request event call for call. Counting both
        // would double every token, so only ttft_ms — which no event carries — is taken.
        var store = new SessionStore();
        var traceId = "aabbccdd";
        var batch = new OtlpBatch();
        batch.Spans.Add(new OtlpSpan
        {
            TraceId = traceId,
            SpanId = "1122",
            Name = "claude_code.llm_request",
            Start = T0.AddSeconds(1),
            End = T0.AddSeconds(3),
            Resource = ClaudeResource(),
            Attributes = new Dictionary<string, AttrValue>
            {
                ["session.id"] = AttrValue.Str("cc-beta"),
                ["ttft_ms"] = AttrValue.Int(640),
                ["model"] = AttrValue.Str("claude-opus-5")
            }
        });
        batch.Logs.Add(new OtlpLogEvent
        {
            EventName = "claude_code.api_request",
            Time = T0.AddSeconds(3),
            TraceId = traceId,
            Resource = ClaudeResource(),
            Attributes = new Dictionary<string, AttrValue>
            {
                ["session.id"] = AttrValue.Str("cc-beta"),
                ["model"] = AttrValue.Str("claude-opus-5"),
                ["input_tokens"] = AttrValue.Int(500),
                ["output_tokens"] = AttrValue.Int(120)
            }
        });

        store.Ingest(batch);

        var session = store.Get("cc-beta")!;
        Assert.Equal(1, session.ChatCalls);        // the span did not add a second one
        Assert.Equal(500, session.InputTokens);
        Assert.Equal(640d, Assert.Single(session.TtftMs));
    }

    [Fact]
    public void InteractionSpanIsTheAgentInvocationAndSharesTheEventsTurn()
    {
        var store = new SessionStore();
        var traceId = "beefbeef";
        var batch = new OtlpBatch();
        batch.Spans.Add(new OtlpSpan
        {
            TraceId = traceId,
            SpanId = "3344",
            Name = "claude_code.interaction",
            Start = T0,
            End = T0.AddSeconds(12),
            Resource = ClaudeResource(),
            Attributes = new Dictionary<string, AttrValue> { ["session.id"] = AttrValue.Str("cc-int") }
        });
        batch.Logs.Add(new OtlpLogEvent
        {
            EventName = "claude_code.user_prompt",
            Time = T0,
            TraceId = traceId,
            Resource = ClaudeResource(),
            Attributes = new Dictionary<string, AttrValue>
            {
                ["session.id"] = AttrValue.Str("cc-int"),
                ["prompt.id"] = AttrValue.Str("p-1")
            }
        });

        store.Ingest(batch);

        var session = store.Get("cc-int")!;
        Assert.Equal(1, session.AgentInvocations);
        // Events carry the interaction span's trace id when tracing is on, so keying turns
        // on it first keeps span-derived and event-derived turns in one numbering.
        Assert.Single(session.TurnList);
        Assert.Equal(1, session.Turns);
    }
}
