<#
.SYNOPSIS
    Enables OpenTelemetry export from GitHub Copilot CLI to CopilotScope.

.DESCRIPTION
    Sets the environment variables Copilot CLI reads for OTel export in the
    CURRENT PowerShell session. Launch `copilot` from this same session
    afterwards — env vars do not reach terminals opened elsewhere.

    IMPORTANT — content capture variable:
    `COPILOT_OTEL_CAPTURE_CONTENT` is NOT a real variable and silently does
    nothing (this is why "it doesn't work"). Copilot CLI follows the OTel GenAI
    standard instead: OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true.
    (In VS Code the equivalent is the *setting*
    github.copilot.chat.otel.captureContent — not an env var either.)

    Also note: the OTEL_EXPORTER_OTLP_*_PROTOCOL variables are largely
    redundant for the CLI — its runtime supports otlp-http only, and even a
    grpc value silently falls back to HTTP. They are set here anyway for
    explicitness and for any other OTel-aware tools in the same shell.

.PARAMETER Endpoint
    OTLP endpoint of the CopilotScope collector. Default: http://localhost:4318

.PARAMETER CaptureContent
    Also export prompt/response/tool content (sensitive!). Default: off.

.PARAMETER ApiKey
    Optional x-api-key for a collector running in Production mode.

.PARAMETER Persist
    Also store the variables at User scope so they survive new terminals.
    (Use .\Enable-CopilotOtel.ps1 -Disable -Persist to clean up later.)

.PARAMETER Disable
    Removes all the variables instead of setting them.

.EXAMPLE
    .\Enable-CopilotOtel.ps1
    copilot

.EXAMPLE
    .\Enable-CopilotOtel.ps1 -CaptureContent -Endpoint http://localhost:4318

.EXAMPLE
    .\Enable-CopilotOtel.ps1 -Endpoint https://copilotscope.example.com -ApiKey $env:SCOPE_KEY
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
    COPILOT_OTEL_ENABLED                              = 'true'
    COPILOT_OTEL_EXPORTER_TYPE                        = 'otlp-http'   # the CLI runtime supports otlp-http only
    OTEL_EXPORTER_OTLP_ENDPOINT                       = $Endpoint
    OTEL_EXPORTER_OTLP_PROTOCOL                       = 'http/protobuf'
    OTEL_EXPORTER_OTLP_TRACES_PROTOCOL                = 'http/protobuf'
    OTEL_EXPORTER_OTLP_METRICS_PROTOCOL               = 'http/protobuf'
    OTEL_EXPORTER_OTLP_LOGS_PROTOCOL                  = 'http/protobuf'
    # The REAL content-capture switch (OTel GenAI standard). There is no
    # COPILOT_OTEL_CAPTURE_CONTENT — setting it has no effect.
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
    Write-Host 'Copilot OTel export disabled (variables removed' -NoNewline
    if ($Persist) { Write-Host ' from this session and User scope).' } else { Write-Host ' from this session).' }
    return
}

Write-Host 'Copilot CLI OTel export configured:' -ForegroundColor Green
Write-Host "  Endpoint        : $Endpoint"
Write-Host "  Protocol        : otlp-http (http/protobuf)"
Write-Host "  Content capture : $(if ($CaptureContent) { 'ON  (prompts/responses will be exported!)' } else { 'off (metadata only)' })"
Write-Host "  Auth header     : $(if ($ApiKey) { 'x-api-key set' } else { 'none' })"
Write-Host "  Scope           : $(if ($Persist) { 'this session + User (persistent)' } else { 'this session only' })"
Write-Host ''
Write-Host 'Now run `copilot` from THIS terminal. Sessions appear in CopilotScope.'
