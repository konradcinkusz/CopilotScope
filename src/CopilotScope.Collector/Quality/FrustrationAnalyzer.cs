using System.Text;
using System.Text.Json;
using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Quality;

/// <summary>
/// #9 Frustration classification — simplified, LLM-free implementation.
///
/// Works on captured user prompts (requires content capture on the client) and
/// combines four transparent signals per message:
///   · lexicon hits, weighted strong/mild, bilingual EN/PL
///   · rephrasing — Jaccard word-set similarity with the previous user message
///     (asking nearly the same thing again is the classic repair signal)
///   · typography — sustained CAPS, bursts of ?!
///   · short corrective replies ("no.", "źle, popraw")
///
/// Deliberately REPORT-ONLY: it does not feed the composite quality score.
/// A lexicon heuristic is noisy (false positives like "no worries", language
/// bias, sarcasm-blind) — every flagged message therefore carries its reasons,
/// so the human can judge. Promote it into the composite only after validating
/// it against your own sessions; SPUR-style learned rubrics are the upgrade
/// path when an LLM budget is acceptable.
/// </summary>
public sealed class FrustrationAnalyzer : IInsightAnalyzer
{
    private static readonly string[] Strong =
    [
        "doesn't work", "does not work", "not working", "still broken", "still wrong",
        "wrong again", "useless", "terrible", "wtf", "stop doing", "i give up", "you broke",
        "nie działa", "dalej nie działa", "znowu źle", "bez sensu", "do niczego", "zepsułeś", "poddaję się"
    ];

    private static readonly string[] Mild =
    [
        "no,", "no.", "that's not", "that is not", "wrong", "incorrect", "not what i",
        "again", "still", "undo", "revert", "why did you", "i said", "as i said",
        "nie o to", "źle", "popraw", "cofnij", "jeszcze raz", "przecież", "mówiłem", "pisałem", "nie tego"
    ];

    public InsightReport Analyze(CopilotSession session)
    {
        var prompts = session.Snapshot(s => s.Transcript
            .Where(t => t.Prompt is not null)
            .Select(t => (t.Time, Text: ExtractUserText(t.Prompt!)))
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .ToList());

        if (prompts.Count == 0)
            return new InsightReport("Frustration signals", "Lexical frustration classification (simplified)",
                "no-data", null, [],
                ["Requires content capture — enable captureContent (VS Code) or OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true (CLI)."]);

        var flagged = new List<string>();
        var scores = new List<double>();
        string? previous = null;

        foreach (var (time, text) in prompts)
        {
            var reasons = new List<string>();
            var lower = text.ToLowerInvariant();
            double score = 0;

            var strongHits = Strong.Count(m => lower.Contains(m));
            if (strongHits > 0) { score += 0.45 * Math.Min(strongHits, 2); reasons.Add($"strong marker ×{strongHits}"); }

            var mildHits = Mild.Count(m => lower.Contains(m));
            if (mildHits > 0) { score += 0.15 * Math.Min(mildHits, 3); reasons.Add($"mild marker ×{mildHits}"); }

            if (previous is not null)
            {
                var similarity = Jaccard(previous, lower);
                if (similarity >= 0.6) { score += 0.30; reasons.Add($"rephrasing (similarity {similarity:P0})"); }
            }

            var letters = text.Count(char.IsLetter);
            if (letters >= 12 && (double)text.Count(char.IsUpper) / letters > 0.6)
            { score += 0.15; reasons.Add("sustained CAPS"); }

            if (text.Contains("!!") || text.Contains("??") || text.Contains("?!"))
            { score += 0.10; reasons.Add("punctuation burst"); }

            if (text.Length < 20 && (lower.StartsWith("no") || lower.StartsWith("nie") || lower.StartsWith("stop") || lower.StartsWith("wrong") || lower.StartsWith("źle")))
            { score += 0.20; reasons.Add("short corrective reply"); }

            score = Math.Min(1.0, score);
            scores.Add(score);
            if (score >= 0.3)
            {
                var preview = text.Length > 90 ? text[..90] + "…" : text;
                flagged.Add($"{time.ToLocalTime():HH:mm:ss} [{score:P0}] \"{preview}\" — {string.Join(", ", reasons)}");
            }
            previous = lower;
        }

        // Session index: mean pulled halfway toward the peak, so one furious
        // message isn't averaged away by ten calm ones.
        var mean = scores.Average();
        var index = mean + 0.5 * (scores.Max() - mean);

        var metrics = new List<InsightMetric>
        {
            new("frustration index (0=calm)", $"{index:P0}"),
            new("messages analyzed / flagged", $"{prompts.Count} / {flagged.Count}"),
            new("peak message score", $"{scores.Max():P0}")
        };

        var findings = new List<string>();
        findings.Add(index switch
        {
            >= 0.5 => "Clear frustration signals — review the flagged messages below.",
            >= 0.25 => "Mild friction detected in the conversation tone.",
            _ => "No meaningful frustration signals."
        });
        findings.AddRange(flagged.Take(5));
        findings.Add("Heuristic, report-only signal — not part of the composite score. Verify flags before acting on them.");

        return new InsightReport("Frustration signals", "Lexical frustration classification (simplified)",
            "ok", index, metrics, findings);
    }

    /// <summary>Pulls user-role text out of raw captured content (JSON message arrays or plain text).</summary>
    internal static string ExtractUserText(string raw)
    {
        var text = raw.Trim();
        if (!text.StartsWith('[') && !text.StartsWith('{')) return text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var sb = new StringBuilder();
            void Walk(JsonElement el)
            {
                switch (el.ValueKind)
                {
                    case JsonValueKind.Array:
                        foreach (var item in el.EnumerateArray()) Walk(item);
                        break;
                    case JsonValueKind.Object:
                        var role = el.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                            ? r.GetString() : null;
                        if (role is not null && !role.Equals("user", StringComparison.OrdinalIgnoreCase)) return;
                        if (el.TryGetProperty("content", out var c))
                        {
                            if (c.ValueKind == JsonValueKind.String) sb.AppendLine(c.GetString());
                            else Walk(c);
                        }
                        else if (el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                            sb.AppendLine(t.GetString());
                        else if (el.TryGetProperty("parts", out var p)) Walk(p);
                        break;
                    case JsonValueKind.String:
                        sb.AppendLine(el.GetString());
                        break;
                }
            }
            Walk(doc.RootElement);
            var extracted = sb.ToString().Trim();
            return extracted.Length > 0 ? extracted : text;
        }
        catch (JsonException) { return text; }
    }

    private static double Jaccard(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);
        if (setA.Count == 0 || setB.Count == 0) return 0;
        var intersection = setA.Intersect(setB).Count();
        return (double)intersection / (setA.Count + setB.Count - intersection);

        static HashSet<string> Tokenize(string s) =>
            s.Split(new[] { ' ', '\n', '\t', ',', '.', '!', '?', ';', ':' }, StringSplitOptions.RemoveEmptyEntries)
             .Where(w => w.Length > 2)
             .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
