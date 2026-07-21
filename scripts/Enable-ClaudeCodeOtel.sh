#!/usr/bin/env bash
# Enable OpenTelemetry export from Claude Code CLI to CopilotScope.
#
# Usage:
#   source ./scripts/Enable-ClaudeCodeOtel.sh              # metadata only
#   source ./scripts/Enable-ClaudeCodeOtel.sh --capture    # include prompt/response content
#   source ./scripts/Enable-ClaudeCodeOtel.sh --endpoint http://host:4318
#   source ./scripts/Enable-ClaudeCodeOtel.sh --disable
#
# IMPORTANT: source (not execute) this script so the env vars are set in
# the current shell, then launch `claude` from the same terminal.

ENDPOINT="http://localhost:4318"
CAPTURE_CONTENT=""
DISABLE=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --endpoint) ENDPOINT="$2"; shift 2 ;;
        --capture)  CAPTURE_CONTENT="true"; shift ;;
        --disable)  DISABLE="true"; shift ;;
        --api-key)  API_KEY="$2"; shift 2 ;;
        *) echo "Unknown option: $1"; shift ;;
    esac
done

if [[ -n "$DISABLE" ]]; then
    unset OTEL_EXPORTER_OTLP_ENDPOINT
    unset OTEL_EXPORTER_OTLP_PROTOCOL
    unset OTEL_EXPORTER_OTLP_TRACES_PROTOCOL
    unset OTEL_EXPORTER_OTLP_METRICS_PROTOCOL
    unset OTEL_EXPORTER_OTLP_LOGS_PROTOCOL
    unset OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT
    unset OTEL_EXPORTER_OTLP_HEADERS
    echo "Claude Code OTel export disabled."
    return 0 2>/dev/null || exit 0
fi

export OTEL_EXPORTER_OTLP_ENDPOINT="$ENDPOINT"
export OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"
export OTEL_EXPORTER_OTLP_TRACES_PROTOCOL="http/protobuf"
export OTEL_EXPORTER_OTLP_METRICS_PROTOCOL="http/protobuf"
export OTEL_EXPORTER_OTLP_LOGS_PROTOCOL="http/protobuf"

if [[ -n "$CAPTURE_CONTENT" ]]; then
    export OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT="true"
else
    unset OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT
fi

if [[ -n "$API_KEY" ]]; then
    export OTEL_EXPORTER_OTLP_HEADERS="x-api-key=$API_KEY"
else
    unset OTEL_EXPORTER_OTLP_HEADERS
fi

echo "Claude Code OTel export configured:"
echo "  Endpoint        : $ENDPOINT"
echo "  Protocol        : http/protobuf"
echo "  Content capture : ${CAPTURE_CONTENT:-off (metadata only)}"
echo "  Auth header     : $([ -n "$API_KEY" ] && echo "x-api-key set" || echo "none")"
echo ""
echo "Now run 'claude' from THIS terminal. Sessions appear in CopilotScope."
