<#
.SYNOPSIS
    Enables OpenTelemetry export from Claude Code CLI to CopilotScope.

.DESCRIPTION
    Sets the environment variables Claude Code reads for OTel export in the
    CURRENT PowerShell session. Launch `claude` from this same session
    afterwards — env vars do not reach terminals opened elsewhere.

    Claude Code follows standard OTel GenAI semantic conventions:
    - OTEL_EXPORTER_OTLP_ENDPOINT points at the CopilotScope collector.
    - OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true enables
      prompt/response content capture (off by default for privacy).

    The collector identifies Claude Code sessions via service.name="claude-code"
    and normalizes claude_code.* attributes into the shared copilot_chat.*
    aggregation schema.

.PARAMETER Endpoint
    OTLP endpoint of the CopilotScope collector. Default: http://localhost:4318

.PARAMETER CaptureContent
    Also export prompt/response/tool content (sensitive!). Default: off.

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
    .\Enable-ClaudeCodeOtel.ps1 -CaptureContent -Endpoint http://localhost:4318

.EXAMPLE
    .\Enable-ClaudeCodeOtel.ps1 -Endpoint https://copilotscope.example.com -ApiKey $env:SCOPE_KEY
#>
[CmdletBinding()]
param(
    [string] $Endpoint = 'http://localhost:4318',
    [switch] $CaptureContent,
    [string] $ApiKey,
    [switch] $Persist,
    [switch] $Disable
)

$vars = [ordered]@{
    OTEL_EXPORTER_OTLP_ENDPOINT                       = $Endpoint
    OTEL_EXPORTER_OTLP_PROTOCOL                       = 'http/protobuf'
    OTEL_EXPORTER_OTLP_TRACES_PROTOCOL                = 'http/protobuf'
    OTEL_EXPORTER_OTLP_METRICS_PROTOCOL               = 'http/protobuf'
    OTEL_EXPORTER_OTLP_LOGS_PROTOCOL                  = 'http/protobuf'
    OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT = $(if ($CaptureContent) { 'true' } else { $null })
    OTEL_EXPORTER_OTLP_HEADERS                        = $(if ($ApiKey) { "x-api-key=$ApiKey" } else { $null })
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
Write-Host "  Content capture : $(if ($CaptureContent) { 'ON  (prompts/responses will be exported!)' } else { 'off (metadata only)' })"
Write-Host "  Auth header     : $(if ($ApiKey) { 'x-api-key set' } else { 'none' })"
Write-Host "  Scope           : $(if ($Persist) { 'this session + User (persistent)' } else { 'this session only' })"
Write-Host ''
Write-Host 'Now run `claude` from THIS terminal. Sessions appear in CopilotScope.'
