using System.Text;
using System.Text.Json;

namespace CopilotScope.Dashboard.Services;

public sealed record ChatMessage(string Role, string Text);

/// <summary>
/// Captured content arrives as whatever the emitter put into
/// gen_ai.input/output.messages — usually a JSON array of role-tagged messages
/// (several dialects exist: content-as-string, content-as-parts, parts/text).
/// This parser normalizes all of them into (role, text) pairs and degrades
/// gracefully to a single raw-text message when the payload isn't JSON.
/// </summary>
public static class ChatMessageParser
{
    public static List<ChatMessage> Parse(string? raw, string fallbackRole)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var text = raw.Trim();

        if (text.StartsWith('[') || text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var messages = new List<ChatMessage>();
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var element in doc.RootElement.EnumerateArray())
                        Add(element, messages, fallbackRole);
                else
                    Add(doc.RootElement, messages, fallbackRole);

                if (messages.Count > 0) return messages;
            }
            catch (JsonException) { /* not JSON after all — fall through to raw */ }
        }

        return [new ChatMessage(fallbackRole, text)];
    }

    private static void Add(JsonElement element, List<ChatMessage> messages, string fallbackRole)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            messages.Add(new(fallbackRole, element.GetString() ?? ""));
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new(fallbackRole, element.ToString()));
            return;
        }

        var role = element.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()!.ToLowerInvariant()
            : fallbackRole;

        string? body = null;
        if (element.TryGetProperty("content", out var content))
            body = content.ValueKind == JsonValueKind.String ? content.GetString() : Flatten(content);
        else if (element.TryGetProperty("parts", out var parts))
            body = Flatten(parts);
        else if (element.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            body = t.GetString();

        if (!string.IsNullOrWhiteSpace(body))
            messages.Add(new(role, body.Trim()));
    }

    private static string Flatten(JsonElement parts)
    {
        switch (parts.ValueKind)
        {
            case JsonValueKind.String:
                return parts.GetString() ?? "";
            case JsonValueKind.Array:
            {
                var sb = new StringBuilder();
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String) sb.AppendLine(part.GetString());
                    else if (part.ValueKind == JsonValueKind.Object)
                    {
                        if (part.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                            sb.AppendLine(c.GetString());
                        else if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                            sb.AppendLine(t.GetString());
                        // tool calls / non-text parts: show a compact marker instead of raw JSON
                        else if (part.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String)
                            sb.AppendLine($"[{ty.GetString()}]");
                    }
                }
                return sb.ToString().Trim();
            }
            default:
                return parts.GetRawText();
        }
    }
}
