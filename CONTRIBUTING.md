# Contributing to CopilotScope

Thank you for your interest in CopilotScope! This document describes how to set up a development environment, run tests, and submit changes.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | 8.0+ | `dotnet --version` to verify |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | any recent | needed for Postgres + pgAdmin containers via Aspire |
| [.NET Aspire workload](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/setup-tooling) | 9.3 | `dotnet workload install aspire` |

## Quick dev loop

```bash
# clone
git clone https://github.com/konradcinkusz/copilotscope.git
cd copilotscope

# restore & build everything
dotnet build

# run the full stack (Postgres + pgAdmin + Collector + Dashboard)
dotnet run --project src/CopilotScope.AppHost

# open the Aspire dashboard (shown in console output) and
# open the CopilotScope dashboard at http://localhost:5XXX

# send synthetic telemetry to see data immediately
dotnet run --project tools/CopilotScope.TelemetryGen
```

## Running tests

```bash
dotnet test
```

All tests live in `tests/CopilotScope.Tests`. They run without Docker or a live collector.

## Project layout

```
src/
  CopilotScope.AppHost/       Aspire orchestration (containers, ports, env)
  CopilotScope.Collector/     OTLP/HTTP ingest, session aggregation, quality engine, REST API
  CopilotScope.Dashboard/     Blazor Server UI (zero JS dependencies)
tests/
  CopilotScope.Tests/         xUnit tests — decoder, routing, quality, persistence
tools/
  CopilotScope.TelemetryGen/  realistic demo telemetry generator
```

## Submitting changes

1. **Fork** the repository and create a feature branch from `main`.
2. Keep changes focused — one logical change per PR.
3. Add or update tests for any new logic in `CopilotScope.Collector`.
4. Run `dotnet test` and `dotnet build` before pushing.
5. Open a pull request against `main`. The PR description should explain *why* the change is needed, not just what it does.

## Architecture notes

- **Session aggregation** happens in `CopilotSession` (mutable, lock-guarded) inside `SessionStore`. All mutations go through `Apply()`.
- **Quality scoring** (`QualityEngine`) and **turn analysis** (`SegmentAnalyzer`) are pure functions over session snapshots — no side effects.
- **Persistence** is a single JSONB column per session in Postgres. The `PersistedSession` record mirrors `CopilotSession` exactly; adding a new field to either requires updating both and the `ToSession()` / `From()` conversions.
- **Dashboard DTOs** live in `CollectorClient.cs` (Dashboard project) and must stay in sync with the collector's `Dtos.cs`. There is no shared assembly by design — the JSON contract is the boundary.
- **No JS frameworks** — the dashboard is Blazor Server with inline CSS and vanilla JS only for one `scrollToBottom` helper.

## Code style

- C# 12 / .NET 8 idioms (primary constructors, collection expressions, pattern switches).
- No XML doc comments except on non-obvious public APIs.
- No abbreviations in names unless they are domain-standard (`ttft`, `otlp`, `llm`).
- Prefer records for DTOs and value objects; mutable classes only for aggregates that need lock-guarded mutation.

## Questions / ideas

Open a [GitHub Discussion](https://github.com/konradcinkusz/copilotscope/discussions) for design questions or feature proposals before writing a large PR. Bug reports go to [Issues](https://github.com/konradcinkusz/copilotscope/issues).
