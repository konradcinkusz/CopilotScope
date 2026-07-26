#!/usr/bin/env bash
# Enable OpenTelemetry export from Claude Code CLI to CopilotScope.
#
# Usage:
#   source ./scripts/Enable-ClaudeCodeOtel.sh              # metadata only
#   source ./scripts/Enable-ClaudeCodeOtel.sh --capture    # include prompt/response/tool content
#   source ./scripts/Enable-ClaudeCodeOtel.sh --traces     # beta spans (adds time-to-first-token)
#   source ./scripts/Enable-ClaudeCodeOtel.sh --endpoint http://host:4318
#   source ./scripts/Enable-ClaudeCodeOtel.sh --disable
#
# IMPORTANT: source (not execute) this script so the env vars are set in
# the current shell, then launch `claude` from the same terminal.
#
# Claude Code speaks its own dialect, not the OTel GenAI span conventions:
# CLAUDE_CODE_ENABLE_TELEMETRY=1 is the master switch (without it nothing is
# exported at all), a default install emits metrics and log events and no spans,
# and content capture is three separate opt-ins. Traces are a beta flag and are
# the only source of time-to-first-token.
# Reference: https://code.claude.com/docs/en/monitoring-usage

ENDPOINT="http://localhost:4318"
CAPTURE_CONTENT=""
TRACES=""
DISABLE=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --endpoint) ENDPOINT="$2"; shift 2 ;;
        --capture)  CAPTURE_CONTENT="true"; shift ;;
        --traces)   TRACES="true"; shift ;;
        --disable)  DISABLE="true"; shift ;;
        --api-key)  API_KEY="$2"; shift 2 ;;
        *) echo "Unknown option: $1"; shift ;;
    esac
done

if [[ -n "$DISABLE" ]]; then
    unset CLAUDE_CODE_ENABLE_TELEMETRY
    unset CLAUDE_CODE_ENHANCED_TELEMETRY_BETA
    unset OTEL_METRICS_EXPORTER
    unset OTEL_LOGS_EXPORTER
    unset OTEL_TRACES_EXPORTER
    unset OTEL_EXPORTER_OTLP_ENDPOINT
    unset OTEL_EXPORTER_OTLP_PROTOCOL
    unset OTEL_EXPORTER_OTLP_TRACES_PROTOCOL
    unset OTEL_EXPORTER_OTLP_METRICS_PROTOCOL
    unset OTEL_EXPORTER_OTLP_LOGS_PROTOCOL
    unset OTEL_METRIC_EXPORT_INTERVAL
    unset OTEL_LOGS_EXPORT_INTERVAL
    unset OTEL_LOG_USER_PROMPTS
    unset OTEL_LOG_ASSISTANT_RESPONSES
    unset OTEL_LOG_TOOL_DETAILS
    unset OTEL_RESOURCE_ATTRIBUTES
    unset OTEL_EXPORTER_OTLP_HEADERS
    # Set by earlier versions of this script from the OTel GenAI conventions,
    # which Claude Code does not read. Cleaned up here too.
    unset OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT
    echo "Claude Code OTel export disabled."
    return 0 2>/dev/null || exit 0
fi

# Master switch — everything below is inert without it.
export CLAUDE_CODE_ENABLE_TELEMETRY="1"

export OTEL_METRICS_EXPORTER="otlp"
export OTEL_LOGS_EXPORTER="otlp"
export OTEL_EXPORTER_OTLP_ENDPOINT="$ENDPOINT"
export OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"
export OTEL_EXPORTER_OTLP_METRICS_PROTOCOL="http/protobuf"
export OTEL_EXPORTER_OTLP_LOGS_PROTOCOL="http/protobuf"

# Defaults are 60 s for metrics and 5 s for logs. A short session would end before
# the first metric flush, so the dashboard would stay empty for a minute.
export OTEL_METRIC_EXPORT_INTERVAL="10000"
export OTEL_LOGS_EXPORT_INTERVAL="5000"

# Names the emitter for the collector even when the resource is otherwise bare.
export OTEL_RESOURCE_ATTRIBUTES="service.name=claude-code"

if [[ -n "$TRACES" ]]; then
    export CLAUDE_CODE_ENHANCED_TELEMETRY_BETA="1"
    export OTEL_TRACES_EXPORTER="otlp"
    export OTEL_EXPORTER_OTLP_TRACES_PROTOCOL="http/protobuf"
else
    unset CLAUDE_CODE_ENHANCED_TELEMETRY_BETA
    unset OTEL_TRACES_EXPORTER
    unset OTEL_EXPORTER_OTLP_TRACES_PROTOCOL
fi

if [[ -n "$CAPTURE_CONTENT" ]]; then
    export OTEL_LOG_USER_PROMPTS="1"
    export OTEL_LOG_ASSISTANT_RESPONSES="1"
    export OTEL_LOG_TOOL_DETAILS="1"
else
    unset OTEL_LOG_USER_PROMPTS
    unset OTEL_LOG_ASSISTANT_RESPONSES
    unset OTEL_LOG_TOOL_DETAILS
fi

if [[ -n "$API_KEY" ]]; then
    export OTEL_EXPORTER_OTLP_HEADERS="x-api-key=$API_KEY"
else
    unset OTEL_EXPORTER_OTLP_HEADERS
fi

echo "Claude Code OTel export configured:"
echo "  Endpoint        : $ENDPOINT"
echo "  Protocol        : http/protobuf"
echo "  Signals         : metrics + logs$([ -n "$TRACES" ] && echo " + traces (beta)")"
echo "  Content capture : $([ -n "$CAPTURE_CONTENT" ] && echo "ON  (prompts/responses/tool args will be exported!)" || echo "off (metadata only)")"
echo "  Auth header     : $([ -n "$API_KEY" ] && echo "x-api-key set" || echo "none")"
echo ""
echo "Now run 'claude' from THIS terminal. Sessions appear in CopilotScope."
echo "The Claude desktop app (Cowork) is configured in its own UI, not here — see docs/TUTORIAL.md."
