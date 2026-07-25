# Security Policy

## Reporting a vulnerability

Report privately through GitHub's
[security advisory form](https://github.com/konradcinkusz/copilotscope/security/advisories/new).
Please do not open a public issue for a vulnerability.

Expect an acknowledgement within a week. This is a single-maintainer project — if
a fix will take longer than that, you will be told so rather than left waiting.

## Supported versions

The latest release only. There are no maintained back-branches.

## What CopilotScope handles

Worth knowing before you deploy it, because the sensitivity depends entirely on
one setting:

- **Metadata only (default).** Token counts, latencies, model names, tool names,
  error types. No prompt or response text.
- **With content capture enabled** (`captureContent` on the client, or
  `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true`), prompt and response
  text arrives in span attributes and is stored in the session snapshot — bounded
  to the last 100 entries, 4 000 chars each. That text can contain source code,
  credentials a developer pasted into a chat, and customer data. Treat the Postgres
  volume and the dashboard as carrying whatever your developers typed.

## Deployment notes

- `/v1/*`, `/api/admin/seed` and `/metrics` are guarded by
  `CopilotScope__Ingest__ApiKey` when it is set. **When it is not set, ingest is
  open** — the default suits localhost, not a shared host.
- `/api/sessions`, `/api/overview` and the dashboard have no authentication of
  their own. Do not expose them beyond localhost without putting your own
  authentication in front.
- `docker-compose.grafana.yml` runs Grafana with anonymous Admin and no login form.
  That is a local-demo convenience; remove the `GF_AUTH_ANONYMOUS_*` settings for
  anything shared.
- The seed endpoint can fabricate session data. It shares the ingest key
  deliberately: anyone who can post fake OTLP can already fabricate sessions, so
  it does not widen the trust boundary — but it does mean the ingest key is enough
  to pollute the dataset.
