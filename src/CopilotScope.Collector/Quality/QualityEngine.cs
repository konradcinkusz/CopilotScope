using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Quality;

/// <summary>
/// Composite 0–100 session quality score, recalculated on every ingest.
///
/// v2 design notes — why not a fixed prior:
/// v1 let components without data contribute a neutral 0.7 at full weight, which
/// pinned every ordinary session (no edit/feedback telemetry yet) to ~79–80 and
/// destroyed discrimination. v2 only aggregates components that actually have
/// data and renormalizes the weights across them; missing components are still
/// reported (samples=0) but carry zero influence. A session with literally no
/// signals gets the neutral prior of 70 at confidence 0.
///
/// Base weights (renormalized over informative components):
///   reliability 0.25 — squared error-free rate (errors bite quadratically)
///   acceptance  0.20 — accepted vs rejected edits + code survival
///   friction    0.20 — mean turn score from the TFRA turn model (repair loops,
///                      error clustering, latency outliers vs session median)
///   latency     0.15 — TTFT p50 on a log-linear [0.3s..10s] curve
///   feedback    0.10 — explicit thumbs up/down
///   efficiency  0.10 — prompt-cache hit ratio + turns-per-invocation sanity
///
/// Confidence = (weight coverage of informative components) × (sample ramp),
/// so "score 91 at confidence 0.2" reads as "early but promising", not "certain".
/// </summary>
public sealed class QualityEngine
{
    private const double Prior = 0.70;

    public QualityReport Evaluate(CopilotSession s) => s.Snapshot(Compute);

    private static QualityReport Compute(CopilotSession s)
    {
        var components = new List<QualityComponent>();

        // ---- Reliability -----------------------------------------------------
        var calls = s.ChatCalls + s.ToolCalls;
        if (calls > 0)
        {
            var weightedErrors = s.ChatErrors * 2.0 + s.ToolErrors;
            var weightedCalls = s.ChatCalls * 2.0 + s.ToolCalls;
            var errorFree = Math.Clamp(1.0 - weightedErrors / Math.Max(1, weightedCalls), 0, 1);
            components.Add(new("reliability", 0.25, errorFree * errorFree, calls,
                $"{s.ChatErrors} LLM err / {s.ChatCalls} calls, {s.ToolErrors} tool err / {s.ToolCalls} calls"));
        }
        else components.Add(new("reliability", 0.25, Prior, 0, "no calls yet"));

        // ---- Acceptance --------------------------------------------------------
        var editSamples = s.EditsAccepted + s.EditsRejected;
        if (editSamples > 0 || s.SurvivalScores.Count > 0)
        {
            var accRatio = editSamples > 0 ? (double)s.EditsAccepted / editSamples : Prior;
            var survival = s.SurvivalScores.Count > 0 ? s.SurvivalScores.Average() : accRatio;
            components.Add(new("acceptance", 0.20, Math.Clamp(0.6 * accRatio + 0.4 * survival, 0, 1),
                editSamples + s.SurvivalScores.Count,
                $"{s.EditsAccepted}✓ / {s.EditsRejected}✗ edits" +
                (s.SurvivalScores.Count > 0 ? $", survival {s.SurvivalScores.Average():P0}" : "")));
        }
        else components.Add(new("acceptance", 0.20, Prior, 0, "no edit telemetry"));

        // ---- Friction (turn-level, TFRA-aligned) -------------------------------
        var turns = s.TurnList.Where(t => t.ChatCalls + t.ToolCalls > 0).ToList();
        if (turns.Count > 0)
        {
            var friction = turns.Average(TurnScore);
            var worst = turns.Min(TurnScore);
            components.Add(new("friction", 0.20, Math.Clamp(friction, 0, 1), turns.Count,
                $"{turns.Count} turns, mean {friction:P0}, worst {worst:P0}"));
        }
        else components.Add(new("friction", 0.20, Prior, 0, "no completed turns"));

        // ---- Latency -----------------------------------------------------------
        if (s.TtftMs.Count > 0)
        {
            var p50 = CopilotSession.Percentile(s.TtftMs, 0.5);
            // <=300 ms → 1.0, >=10 000 ms → 0.0, log-linear in between.
            var latency = p50 <= 300 ? 1.0
                        : p50 >= 10_000 ? 0.0
                        : 1.0 - Math.Log(p50 / 300.0) / Math.Log(10_000.0 / 300.0);
            components.Add(new("latency", 0.15, Math.Clamp(latency, 0, 1), s.TtftMs.Count,
                $"TTFT p50 {p50:F0} ms"));
        }
        else components.Add(new("latency", 0.15, Prior, 0, "no TTFT samples"));

        // ---- Explicit feedback -------------------------------------------------
        var votes = s.ThumbsUp + s.ThumbsDown;
        if (votes > 0)
            components.Add(new("feedback", 0.10, (double)s.ThumbsUp / votes, votes,
                $"👍{s.ThumbsUp} 👎{s.ThumbsDown}"));
        else components.Add(new("feedback", 0.10, Prior, 0, "no votes"));

        // ---- Efficiency ----------------------------------------------------------
        var promptTokens = s.InputTokens + s.CacheReadTokens;
        if (promptTokens > 0 || s.AgentInvocations > 0)
        {
            var parts = new List<double>();
            if (promptTokens > 0) parts.Add((double)s.CacheReadTokens / promptTokens);
            if (s.AgentInvocations > 0)
            {
                var turnsPerInvocation = (double)s.Turns / s.AgentInvocations;
                parts.Add(turnsPerInvocation <= 8 ? 1.0
                        : Math.Clamp(1.0 - (turnsPerInvocation - 8) / 17.0, 0, 1));
            }
            components.Add(new("efficiency", 0.10, Math.Clamp(parts.Average(), 0, 1),
                (int)Math.Min(int.MaxValue, promptTokens),
                $"cache hit {(promptTokens > 0 ? (double)s.CacheReadTokens / promptTokens : 0):P0}, " +
                $"{(s.AgentInvocations > 0 ? (double)s.Turns / s.AgentInvocations : 0):F1} turns/invocation"));
        }
        else components.Add(new("efficiency", 0.10, Prior, 0, "no token data"));

        // ---- Composite: informative components only, weights renormalized -------
        var informative = components.Where(c => c.Samples > 0).ToList();
        double score, confidence;
        if (informative.Count == 0)
        {
            score = Prior * 100;
            confidence = 0;
        }
        else
        {
            var coverage = informative.Sum(c => c.Weight); // ≤ 1.0
            score = informative.Sum(c => c.Weight * c.Value) / coverage * 100.0;
            var sampleRamp = informative.Sum(c => c.Weight * Math.Min(1.0, c.Samples / 5.0)) / coverage;
            confidence = coverage * sampleRamp;
        }

        return new QualityReport(
            Math.Round(score, 1),
            Math.Round(confidence, 2),
            Grade(score),
            components);
    }

    /// <summary>Per-turn friction score — same penalty model as <see cref="SegmentAnalyzer"/>.</summary>
    private static double TurnScore(TurnStat t)
    {
        var score = 1.0;
        if (t.ChatErrors > 0) score -= 0.35 * Math.Min(t.ChatErrors, 2);
        if (t.ToolErrors > 0) score -= 0.15 * Math.Min(t.ToolErrors, 3);
        if (t.ToolErrors > 0 && t.ToolCalls >= 3 && t.ToolCalls >= 3 * Math.Max(1, t.ChatCalls))
            score -= 0.10; // repair loop
        return Math.Clamp(score, 0, 1);
    }

    private static string Grade(double score) => score switch
    {
        >= 85 => "excellent",
        >= 70 => "good",
        >= 55 => "fair",
        >= 40 => "poor",
        _ => "critical"
    };
}

public sealed record QualityComponent(string Name, double Weight, double Value, int Samples, string Detail);

public sealed record QualityReport(double Score, double Confidence, string Grade, IReadOnlyList<QualityComponent> Components);
