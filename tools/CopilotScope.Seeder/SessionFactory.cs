using CopilotScope.Collector.Domain;

namespace CopilotScope.Seeder;

/// <summary>
/// Builds a fully-populated <see cref="CopilotSession"/> aggregate for one persona: tokens,
/// TTFT/duration samples, per-model usage, per-tool stats, error types, edits/feedback,
/// survival scores, a per-turn breakdown and a captured transcript — every field the
/// dashboard and quality/insight engines read, not just the counters.
/// </summary>
public static class SessionFactory
{
    public static CopilotSession Build(string id, Persona persona, DateTimeOffset start, Random rng)
    {
        var session = new CopilotSession
        {
            Id = id,
            AgentName = "copilot",
            Repository = Fixtures.Repositories[rng.Next(Fixtures.Repositories.Length)],
            Branch = Fixtures.Branches[rng.Next(Fixtures.Branches.Length)],
            EmitterKind = (EmitterKind)rng.Next(1, 5), // VSCode..Cursor — never Unknown
            FirstSeen = start,
        };

        var turnCount = rng.Next(persona.MinTurns, persona.MaxTurns + 1);
        var isInternal = persona.InternalPromptPrefix is not null;
        var clock = start;
        var lastActivity = start;

        var totalEdits = isInternal ? 0 : rng.Next(2, 11);
        var editsAccepted = (int)Math.Round(totalEdits * persona.EditAcceptRatio);

        for (var t = 0; t < turnCount; t++)
        {
            var traceId = $"{id}-t{t}";
            var turn = session.TurnFor(traceId, clock);
            var model = Fixtures.Models[rng.Next(Fixtures.Models.Length)];

            var input = rng.NextInt64(persona.MinInputTokens, persona.MaxInputTokens);
            var output = rng.NextInt64(persona.MinOutputTokens, persona.MaxOutputTokens);
            var cacheRead = (long)(input * rng.NextDouble() * 0.6);
            var ttft = persona.MinTtftMs + rng.NextDouble() * (persona.MaxTtftMs - persona.MinTtftMs);
            var isChatError = rng.NextDouble() < persona.ChatErrorRate;
            var chatDurationMs = ttft + 200 + rng.Next(1500);

            session.ChatCalls++;
            turn.ChatCalls++;
            if (isChatError)
            {
                session.ChatErrors++;
                turn.ChatErrors++;
                var errorType = Fixtures.ErrorTypes[rng.Next(Fixtures.ErrorTypes.Length)];
                session.ErrorTypes.AddOrUpdate(errorType, 1, (_, c) => c + 1);
            }

            session.InputTokens += input;
            session.OutputTokens += output;
            session.CacheReadTokens += cacheRead;
            turn.InputTokens += input;
            turn.OutputTokens += output;

            session.ModelCalls.AddOrUpdate(model, 1, (_, c) => c + 1);
            session.ModelUsage.AddOrUpdate(model,
                new ModelStat { Calls = 1, InputTokens = input, OutputTokens = output, CacheReadTokens = cacheRead },
                (_, e) =>
                {
                    e.Calls++;
                    e.InputTokens += input;
                    e.OutputTokens += output;
                    e.CacheReadTokens += cacheRead;
                    return e;
                });

            session.TtftMs.Add(ttft);
            session.ChatDurationMs.Add(chatDurationMs);
            turn.TtftTotalMs += ttft;
            turn.TtftCount++;
            turn.PrimaryModel ??= model;

            var (prompt, response) = PickTurnText(persona, t, turnCount, rng);
            session.AddTranscript(clock, model, prompt, response, t);
            session.AddEvent(new SessionEvent(clock, "chat",
                $"chat {model} · {input}→{output} tok · {chatDurationMs:F0} ms{(isChatError ? " · ERROR" : "")}"));

            var toolCount = isInternal ? 0 : rng.Next(0, 4);
            var toolClock = clock.AddSeconds(1 + rng.Next(3));
            for (var k = 0; k < toolCount; k++)
            {
                var tool = Fixtures.Tools[rng.Next(Fixtures.Tools.Length)];
                var isToolError = rng.NextDouble() < persona.ToolErrorRate;
                var toolMs = 40 + rng.Next(1600);

                session.ToolCalls++;
                turn.ToolCalls++;
                if (isToolError) { session.ToolErrors++; turn.ToolErrors++; }
                session.Tools.AddOrUpdate(tool,
                    (1, isToolError ? 1 : 0, (double)toolMs),
                    (_, e) => (e.Calls + 1, e.Errors + (isToolError ? 1 : 0), e.TotalMs + toolMs));

                session.AddEvent(new SessionEvent(toolClock, "execute_tool",
                    $"{tool} · {toolMs} ms{(isToolError ? " · ERROR" : "")}"));
                toolClock = toolClock.AddSeconds(1 + rng.Next(2));
            }

            var turnEnd = toolClock.AddSeconds(1 + rng.Next(3));
            turn.End = turnEnd;
            lastActivity = turnEnd;
            session.AgentInvocations++;
            session.AddEvent(new SessionEvent(turnEnd, "invoke_agent", $"invoke_agent copilot · turn {t + 1}"));

            clock = turnEnd.AddSeconds(30 + rng.Next(150));
        }

        session.Turns = turnCount;

        if (!isInternal)
        {
            session.EditsAccepted = editsAccepted;
            session.EditsRejected = totalEdits - editsAccepted;
            session.LinesAdded = rng.Next(10, 200);
            session.LinesRemoved = rng.Next(0, (int)session.LinesAdded / 2 + 1);

            var totalFeedback = rng.Next(0, 3);
            var up = (int)Math.Round(totalFeedback * persona.ThumbsUpRatio);
            session.ThumbsUp = up;
            session.ThumbsDown = totalFeedback - up;

            for (var s = 0; s < turnCount; s++)
            {
                var noRevert = Lerp(persona.MinSurvivalNoRevert, persona.MaxSurvivalNoRevert, rng.NextDouble());
                var fourGram = Lerp(persona.MinSurvivalFourGram, persona.MaxSurvivalFourGram, rng.NextDouble());
                session.SurvivalNoRevert.Add(noRevert);
                session.SurvivalFourGram.Add(fourGram);
                session.SurvivalScores.Add(0.4 * fourGram + 0.6 * noRevert);
            }
        }

        session.LastSeen = lastActivity;
        return session;
    }

    private static (string? Prompt, string? Response) PickTurnText(Persona persona, int turnIndex, int turnCount, Random rng)
    {
        if (persona.InternalPromptPrefix is not null)
        {
            var tail = Fixtures.TitlePromptTails[rng.Next(Fixtures.TitlePromptTails.Length)];
            return (persona.InternalPromptPrefix + tail, "Session summary.");
        }

        // The last two turns of a "frustrated" persona carry recognizable rephrasing +
        // strong-marker language so FrustrationAnalyzer actually flags them.
        if (persona.Frustrated && turnIndex >= turnCount - 2)
        {
            var pick = Fixtures.FrustratedTurns[Math.Min(turnIndex, Fixtures.FrustratedTurns.Length - 1)];
            return (pick.Prompt, pick.Response);
        }

        var t = Fixtures.Turns[rng.Next(Fixtures.Turns.Length)];
        return (t.Prompt, t.Response);
    }

    private static double Lerp(double min, double max, double t) => min + (max - min) * t;
}
