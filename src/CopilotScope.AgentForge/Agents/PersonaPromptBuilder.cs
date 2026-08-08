using System.Text;
using CopilotScope.AgentForge.Domain;

namespace CopilotScope.AgentForge.Agents;

/// <summary>Renders PersonaSystemPromptTemplate.txt against a PersonaProfile. Plain
/// string.Replace placeholder substitution — no templating engine dependency, matching the rest
/// of the repo.</summary>
public sealed class PersonaPromptBuilder
{
    private const string ResourceName = "CopilotScope.AgentForge.Agents.PersonaSystemPromptTemplate.txt";
    private readonly string _template;

    public PersonaPromptBuilder()
    {
        var assembly = typeof(PersonaPromptBuilder).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        _template = reader.ReadToEnd();
    }

    public string Build(PersonaProfile profile)
    {
        return _template
            .Replace("{{DisplayLabel}}", profile.DisplayLabel)
            .Replace("{{AvgQualityScore}}", profile.AvgQualityScore.ToString("0.0"))
            .Replace("{{CommonTools}}", profile.CommonTools.Count > 0
                ? string.Join(", ", profile.CommonTools)
                : "no tool usage recorded")
            .Replace("{{Exemplars}}", RenderExemplars(profile.Exemplars));
    }

    private static string RenderExemplars(List<ExemplarTurn> exemplars)
    {
        if (exemplars.Count == 0) return "(no exemplar sessions available)";

        var sb = new StringBuilder();
        for (var i = 0; i < exemplars.Count; i++)
        {
            var e = exemplars[i];
            sb.AppendLine($"{i + 1}. User: {e.Prompt}");
            sb.AppendLine($"   Response: {e.Response}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
