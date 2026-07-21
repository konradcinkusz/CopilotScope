# CopilotScope — Setup Tutorial

Step-by-step configuration for every Copilot surface that can emit OpenTelemetry,
plus troubleshooting for the most common "everything starts but no sessions appear"
situations.

## 0. Fastest path: the setup wizard

`scripts/setup.sh` / `scripts/setup.ps1` chain everything below into one command:
start the stack, wait for it to be healthy, optionally export CLI env vars into
your current shell, print the exact VS Code snippet for your endpoint/key, and
run a smoke test to confirm telemetry actually reaches the collector.

```bash
# macOS / Linux — source it if you want Copilot CLI / Claude Code env vars
# exported into THIS shell (export only survives in the sourcing shell):
source ./scripts/setup.sh --copilot-cli
```

```powershell
# Windows — env vars land in the current session either way
.\scripts\setup.ps1 -CopilotCli
```

Run `./scripts/setup.sh --help` / `Get-Help .\scripts\setup.ps1 -Full` for all
options (`--mode compose|aspire|skip-start`, `--claude-code`,
`--capture-content`, `--endpoint`, `--api-key`, `--skip-verify`). For VS Code,
the wizard only prints the snippet — you still edit `settings.json` and reload
the window (step 2 below explains why that step can't be automated).

CLI env vars (`--copilot-cli` / `--claude-code`) only live in the shell you
sourced the wizard in — add `--persist` (`-Persist` on Windows) to also write
them to your shell rc file (`~/.zshrc`/`~/.bashrc`, auto-detected) or the
Windows User environment scope, so new terminals pick them up without
re-running anything. Safe to re-run — it replaces its own block instead of
duplicating.

The rest of this document is the manual walkthrough the wizard automates —
useful if you want to understand each step, configure a surface the wizard
doesn't cover yet, or troubleshoot (section 8).

## 1. Start CopilotScope

Pick one of two ways to run it — both expose the same two things: an OTLP ingest
endpoint on **:4318** and the dashboard UI.

**A. .NET Aspire (recommended for development)** — requires .NET 8 SDK + Docker:

```bash
dotnet run --project src/CopilotScope.AppHost
```

The Aspire dashboard opens in your browser. It shows four resources: `postgres`
(container with a persistent volume), `postgres-pgadmin` (browse the `sessions`
table directly from here), `collector` (pinned to http://localhost:4318) and
`dashboard` (click its endpoint link to open the UI).

**B. docker-compose (containers + Postgres + API key):**

```bash
docker compose up --build     # dashboard on :5200, ingest on :4318, key: dev-secret-123
```

Verify the collector is up before configuring any client:

```bash
curl http://localhost:4318/api/health
```

## 2. VS Code (Copilot Chat)

1. Open Settings JSON (`Ctrl+Shift+P` → *Preferences: Open User Settings (JSON)*).
2. Add:

```jsonc
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.otlpEndpoint": "http://localhost:4318",
  "github.copilot.chat.otel.exporterType": "otlp-http",
  // Optional — sends prompt/response text so the dashboard's
  // "Prompts & responses" panel has content. Only in trusted environments:
  "github.copilot.chat.otel.captureContent": true
}
```

3. **Reload the VS Code window** (`Ctrl+Shift+P` → *Developer: Reload Window*).
   Settings are read at extension startup — this step is not optional.
4. Open Copilot Chat and send a message **in Agent mode** (or any chat interaction).
   Plain inline code completions do not produce chat telemetry.
5. The session appears on the CopilotScope dashboard within seconds.

Environment variables (`COPILOT_OTEL_ENABLED=true`, `OTEL_EXPORTER_OTLP_ENDPOINT`)
work too and take precedence over settings — but then VS Code must be **launched
from a shell that has them exported**, not from the taskbar icon.

When OTel is enabled in VS Code, all agent types are instrumented — including
Copilot CLI agents and Claude agents running inside VS Code.

## 3. GitHub Copilot CLI (standalone terminal)

The CLI is configured via environment variables:

```bash
export COPILOT_OTEL_ENABLED=true
export COPILOT_OTEL_EXPORTER_TYPE=otlp-http     # the CLI supports otlp-http only
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
# content capture (optional, sensitive) — note this is the OTel GenAI standard
# variable; COPILOT_OTEL_CAPTURE_CONTENT does NOT exist and silently does nothing:
export OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true
copilot
```

On Windows, `scripts/Enable-CopilotOtel.ps1` sets all of the above in one go
(`-CaptureContent` switch included) and warns about the fake variable trap.

Notes:
- The CLI runtime **only supports otlp-http** — configuring gRPC silently falls
  back to HTTP, which is exactly what CopilotScope ingests.
- CLI traces appear as their own sessions (service `github-copilot`), separate
  from VS Code window sessions.
- Alternative: `COPILOT_OTEL_EXPORTER_TYPE=file` writes JSONL to
  `~/.copilot/otel/` instead of sending anywhere (not used by CopilotScope).

## 4. Copilot SDK (your own apps)

Every SDK language accepts a telemetry config pointing at the collector:

```csharp
var client = new CopilotClient(new CopilotClientOptions
{
    Telemetry = new TelemetryConfig { OtlpEndpoint = "http://localhost:4318" }
});
```

(Equivalent one-liners exist for TypeScript, Python, Go, Java and Rust — see
the Copilot SDK OpenTelemetry docs.)

## 5. Other Copilot surfaces — current status

| Surface | OTel export | How |
|---|---|---|
| VS Code Copilot Chat | ✅ | settings / env / managed settings |
| Copilot CLI | ✅ (otlp-http only) | `COPILOT_OTEL_*` env vars |
| Copilot SDK apps | ✅ | `TelemetryConfig` |
| Claude agents inside VS Code | ✅ | same VS Code settings |
| Copilot coding agent (github.com) | ➖ | runs on GitHub's infra; metrics surface in VS Code's agent-outcome metrics, no direct OTLP to your collector |
| **Visual Studio (2022/2026)** | ❌ | no OTel export as of July 2026 |
| JetBrains / Xcode / Eclipse plugins | ❌ | no OTel export as of July 2026 |
| Copilot Studio (Power Platform) | ➖ | exports OTel-aligned spans, but only to Azure Application Insights (admin-configured), not to arbitrary OTLP endpoints |

## 6. Enterprise: force the configuration centrally

Organizations can mandate the OTLP endpoint through Copilot **managed settings**
(the `telemetry` block), delivered via native MDM (Windows registry / macOS
managed preferences), a server-managed policy on the GitHub account, or
`managed-settings.json` on disk. Managed values override both env vars and user
settings, and can also lock `captureContent`. Precedence: policy → env var →
user setting → default.

## 7. Cloud / team mode

Deploy the collector where the team can reach it (e.g. Azure Container Apps —
`infra/main.bicep`), set an ingest key, and point clients at it:

```jsonc
// VS Code settings.json
"github.copilot.chat.otel.otlpEndpoint": "https://copilotscope.<region>.azurecontainerapps.io"
```

```bash
# auth header — exported before starting VS Code / the CLI
export OTEL_EXPORTER_OTLP_HEADERS="x-api-key=<secret>"
```

## 8. Troubleshooting: "it starts fine but no sessions show up"

Work through these in order — they cover, in practice, every case we've seen:

1. **Did you reload the VS Code window after changing settings?** OTel settings
   are read at startup. `Developer: Reload Window`, then chat again.
2. **Are you actually chatting?** Telemetry comes from *chat/agent* interactions
   (`invoke_agent` → `chat`/`execute_tool` spans). Inline tab-completions alone
   don't create chat sessions.
3. **Check the collector log.** Every accepted batch logs `New session(s)
   started`, and every *rejected* request logs a warning with the reason
   (wrong content type, bad decode, unauthorized). Silence in the log = nothing
   is reaching port 4318 → the problem is on the client side (settings,
   endpoint URL, firewall, VS Code not reloaded).
4. **exporterType mismatch.** CopilotScope ingests OTLP/HTTP protobuf. If you set
   `otlp-grpc` in VS Code, the extension speaks gRPC on your endpoint and the
   collector rejects it (415 in logs). Set `"otlp-http"` (the default).
5. **Compressed payloads** (`Content-Encoding: gzip/deflate`) are handled by the
   collector — if you run an older build, update: this was a real
   "logs look fine, sessions empty" cause.
6. **Env vars overriding settings.** If `OTEL_EXPORTER_OTLP_ENDPOINT` is exported
   in the shell VS Code started from, it wins over settings.json — check
   `echo $OTEL_EXPORTER_OTLP_ENDPOINT`.
7. **Managed settings overriding you.** On a company machine, enterprise policy
   may pin the endpoint to a corporate collector. Managed values always win.
8. **API key mode.** In Production the `/v1/*` routes require `x-api-key`; a
   missing header is a 401 warning in the collector log. Export
   `OTEL_EXPORTER_OTLP_HEADERS="x-api-key=<secret>"` before launching the client.
9. **A second session named "unattributed" appears next to your real one.**
   Fixed in current builds: CLI metrics and logs carry no conversation id (and,
   unlike VS Code, no `session.id` resource attribute), so they used to pile up
   in a permanent "unattributed" bucket — taking edit-acceptance and feedback
   data with them. The collector now maps each emitter (resource fingerprint) to
   its most recent conversation and merges the bucket into it as soon as the
   conversation identifies itself. If you still see one, rebuild the collector
   image; a bucket may also appear briefly at startup before the first
   conversation span arrives — it disappears on its own.
10. **Sanity check the pipeline without Copilot:**
   `dotnet run --project tools/CopilotScope.TelemetryGen -- http://localhost:4318 probe`
   — if `probe` shows up on the dashboard, CopilotScope is healthy and the issue
   is purely client configuration.

## 9. Privacy note

By default Copilot sends **metadata only** (models, tokens, durations, tool
names, error types) — no prompts, no code. The "Prompts & responses" panel stays
empty unless `captureContent` is enabled on the client. Repository URLs are
stripped of embedded credentials before display or storage.
