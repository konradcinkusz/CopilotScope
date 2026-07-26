<#
.SYNOPSIS
    Enables OpenTelemetry export from Claude Code CLI to CopilotScope.

.DESCRIPTION
    Sets the environment variables Claude Code reads for OTel export in the
    CURRENT PowerShell session. Launch `claude` from this same session
    afterwards — env vars do not reach terminals opened elsewhere.

    Claude Code speaks its own dialect, not the OTel GenAI span conventions:

    - CLAUDE_CODE_ENABLE_TELEMETRY=1 is the master switch. Without it Claude
      Code exports nothing at all, whatever else is configured.
    - OTEL_METRICS_EXPORTER / OTEL_LOGS_EXPORTER select the exporters. A
      default install emits metrics and log events and NO spans, so the logs
      exporter is the one that actually carries sessions — CopilotScope reads
      calls, tokens, tools and edit decisions off claude_code.* log events.
    - Content capture is three separate opt-ins (prompts, responses, tool
      details), all off by default because they carry source and secrets.
    - Distributed tracing is a beta behind CLAUDE_CODE_ENHANCED_TELEMETRY_BETA;
      -Traces turns it on and is what gives Claude sessions a time-to-first-token.

    Reference: https://code.claude.com/docs/en/monitoring-usage

.PARAMETER Endpoint
    OTLP endpoint of the CopilotScope collector. Default: http://localhost:4318

.PARAMETER CaptureContent
    Also export prompt, response and tool-call content (sensitive!). Default: off.

.PARAMETER Traces
    Also export the beta trace spans. Adds time-to-first-token, which no Claude
    Code metric or event carries. Beta — the span schema can still change.

.PARAMETER ApiKey
    Optional x-api-key for a collector running in Production mode.

.PARAMETER Persist
    Also store the variables at User scope so they survive new terminals.
    (Use .\Enable-ClaudeCodeOtel.ps1 -Disable -Persist to clean up later.)

.PARAMETER Disable
    Removes all the variables instead of setting them.

.EXAMPLE
    .\Enable-ClaudeCodeOtel.ps1
    claude

.EXAMPLE
    .\Enable-ClaudeCodeOtel.ps1 -CaptureContent -Traces

.EXAMPLE
    .\Enable-ClaudeCodeOtel.ps1 -Endpoint https://copilotscope.example.com -ApiKey $env:SCOPE_KEY
#>
[CmdletBinding()]
param(
    [string] $Endpoint = 'http://localhost:4318',
    [switch] $CaptureContent,
    [switch] $Traces,
    [string] $ApiKey,
    [switch] $Persist,
    [switch] $Disable
)

$vars = [ordered]@{
    # Master switch — everything below is inert without it.
    CLAUDE_CODE_ENABLE_TELEMETRY        = '1'

    OTEL_METRICS_EXPORTER               = 'otlp'
    OTEL_LOGS_EXPORTER                  = 'otlp'
    OTEL_EXPORTER_OTLP_ENDPOINT         = $Endpoint
    OTEL_EXPORTER_OTLP_PROTOCOL         = 'http/protobuf'
    OTEL_EXPORTER_OTLP_METRICS_PROTOCOL = 'http/protobuf'
    OTEL_EXPORTER_OTLP_LOGS_PROTOCOL    = 'http/protobuf'

    # Defaults are 60 s for metrics and 5 s for logs. A short session would end
    # before the first metric flush, so the dashboard would stay empty for a minute.
    OTEL_METRIC_EXPORT_INTERVAL         = '10000'
    OTEL_LOGS_EXPORT_INTERVAL           = '5000'

    # Beta tracing (opt-in): the only source of time-to-first-token.
    CLAUDE_CODE_ENHANCED_TELEMETRY_BETA = $(if ($Traces) { '1' } else { $null })
    OTEL_TRACES_EXPORTER                = $(if ($Traces) { 'otlp' } else { $null })
    OTEL_EXPORTER_OTLP_TRACES_PROTOCOL  = $(if ($Traces) { 'http/protobuf' } else { $null })

    # Content capture (opt-in): prompts, responses and tool arguments.
    OTEL_LOG_USER_PROMPTS               = $(if ($CaptureContent) { '1' } else { $null })
    OTEL_LOG_ASSISTANT_RESPONSES        = $(if ($CaptureContent) { '1' } else { $null })
    OTEL_LOG_TOOL_DETAILS               = $(if ($CaptureContent) { '1' } else { $null })

    OTEL_EXPORTER_OTLP_HEADERS          = $(if ($ApiKey) { "x-api-key=$ApiKey" } else { $null })

    # Names the emitter for the collector even when the resource is otherwise bare.
    OTEL_RESOURCE_ATTRIBUTES            = 'service.name=claude-code'

    # Set by earlier versions of this script from the OTel GenAI conventions, which
    # Claude Code does not read. Listed so -Disable still cleans it up.
    OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT = $null
}

foreach ($name in $vars.Keys) {
    $value = if ($Disable) { $null } else { $vars[$name] }

    Set-Item -Path "Env:$name" -Value $value -ErrorAction SilentlyContinue
    if ($null -eq $value) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }

    if ($Persist) {
        [Environment]::SetEnvironmentVariable($name, $value, 'User')
    }
}

if ($Disable) {
    Write-Host 'Claude Code OTel export disabled (variables removed' -NoNewline
    if ($Persist) { Write-Host ' from this session and User scope).' } else { Write-Host ' from this session).' }
    return
}

Write-Host 'Claude Code OTel export configured:' -ForegroundColor Green
Write-Host "  Endpoint        : $Endpoint"
Write-Host "  Protocol        : http/protobuf"
Write-Host "  Signals         : metrics + logs$(if ($Traces) { ' + traces (beta)' })"
Write-Host "  Content capture : $(if ($CaptureContent) { 'ON  (prompts/responses/tool args will be exported!)' } else { 'off (metadata only)' })"
Write-Host "  Auth header     : $(if ($ApiKey) { 'x-api-key set' } else { 'none' })"
Write-Host "  Scope           : $(if ($Persist) { 'this session + User (persistent)' } else { 'this session only' })"
Write-Host ''
Write-Host 'Now run `claude` from THIS terminal. Sessions appear in CopilotScope.'
Write-Host 'The Claude desktop app (Cowork) is configured in its own UI, not here — see docs/TUTORIAL.md.'
