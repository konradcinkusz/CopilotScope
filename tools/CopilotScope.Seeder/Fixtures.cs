namespace CopilotScope.Seeder;

/// <summary>Fixed pools of realistic-looking demo data — models, repos, tools, prompt/response
/// pairs — drawn from randomly by <see cref="SessionFactory"/> to make each seeded session
/// look like a distinct, plausible Copilot conversation rather than a templated clone.</summary>
public static class Fixtures
{
    public static readonly string[] Models =
        ["claude-sonnet-5", "claude-opus-4", "claude-haiku-4-5", "gpt-4o", "claude-3-5-sonnet"];

    public static readonly string[] Repositories =
    [
        "https://github.com/acme/aurelius-promptus",
        "https://github.com/acme/billing-service",
        "https://github.com/acme/frontend-shell",
        "https://github.com/acme/infra-terraform",
        "https://github.com/acme/mobile-app",
    ];

    public static readonly string[] Branches =
        ["main", "feature/otel-dashboard", "fix/retry-policy", "chore/deps-bump", "feature/search-v2", "hotfix/nullref"];

    public static readonly string[] Tools =
        ["readFile", "editFile", "runCommand", "grepSearch", "listDirectory", "createFile", "semanticSearch"];

    public static readonly string[] ErrorTypes =
        ["RateLimitError", "ToolExecutionError", "ContextLengthExceeded", "NetworkTimeout"];

    public static readonly (string Prompt, string Response)[] Turns =
    [
        ("Refactor SessionStore.Ingest to avoid double-counting tokens.",
         "Updated the aggregation so chat spans are the single token source; invoke_agent totals are ignored."),
        ("Why does the OTLP decoder return an empty batch for this payload?",
         "The payload was gzip-compressed; the request body must be decompressed before protobuf decoding."),
        ("Add a retry policy with exponential backoff to the forwarder.",
         "Added a bounded channel with 3 retries and exponential backoff starting at 500 ms."),
        ("Write unit tests for the quality engine prior blending.",
         "Added tests covering the empty-session prior and the confidence ramp at 5 samples."),
        ("Explain the difference between gauge and sum metric points.",
         "A gauge reports the last value; a sum accumulates deltas or cumulative totals over time."),
        ("Can you fix the null reference in the dashboard session list?",
         "Guarded the null repository case and added a fallback label for anonymous sessions."),
        ("Optimize the Postgres upsert to batch multiple sessions at once.",
         "Switched to a single multi-row INSERT ... ON CONFLICT statement."),
        ("Add pagination to the /api/sessions endpoint.",
         "Added limit/offset query parameters with a default page size of 50."),
        ("Investigate why TTFT samples look bimodal in the chart.",
         "Found two code paths reporting TTFT with different units; normalized both to milliseconds."),
        ("Split the SegmentAnalyzer into per-turn and per-session scoring.",
         "Extracted a TurnScorer helper; SegmentAnalyzer now just ranks its output."),
    ];

    public static readonly (string Prompt, string Response)[] FrustratedTurns =
    [
        ("This still doesn't work, the tests are failing again.",
         "Sorry about that — let me look at the failure more carefully."),
        ("Wrong again!! I already told you the bug is in the decoder, not the store.",
         "You're right, apologies — fixing the decoder path now."),
        ("Please add a retry policy to the forwarder queue.",
         "Added a bounded retry with backoff."),
        ("add a retry policy to the forwarder queue please",
         "Done, same change applied with jitter added this time."),
    ];

    public static readonly string[] TitlePromptTails =
    [
        "write 5 paragraphs about the release notes",
        "explain the retry policy change to a new teammate",
        "summarize the OTLP decoder bug we just fixed",
    ];
}
