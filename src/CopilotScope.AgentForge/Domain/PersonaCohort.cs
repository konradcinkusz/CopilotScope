namespace CopilotScope.AgentForge.Domain;

/// <summary>
/// Explicit, manually-authored link between a persona and the session ids that ground it.
/// This is the ONLY way AgentForge associates sessions with a person — there is no code path
/// that infers this from telemetry (the Collector stores no per-person identity by design).
///
/// Deliberately a plain mutable class, not a positional record: Microsoft.Extensions.Configuration
/// binds this type from CopilotScope:AgentForge:Cohorts, and binding a record (or any type with a
/// matching parameterized constructor) that also has a List&lt;T&gt; property double-binds that
/// list — the config binder appends to the list the constructor already populated, silently
/// duplicating every session id. Plain settable properties avoid the constructor-matching path
/// entirely, so the binder only sets each property once.
/// </summary>
public sealed class PersonaCohort
{
    public string PersonaId { get; set; } = "";
    public string DisplayLabel { get; set; } = "";
    public string ConsentGrantedBy { get; set; } = "";
    public DateOnly ConsentDate { get; set; }
    public List<string> SessionIds { get; set; } = new();
}
