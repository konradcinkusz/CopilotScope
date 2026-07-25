<!-- Explain *why* the change is needed, not just what it does. -->

## Why

## What changed

## Checks

- [ ] `dotnet build` and `dotnet test` pass locally (CI runs both)
- [ ] New logic in `CopilotScope.Collector` has tests
- [ ] If a scoring rule changed: the rationale is documented, and the effect on
      existing sessions is described (scores are compared over time — a silent
      recalibration invalidates a user's history)
- [ ] If a DTO changed: `Collector/Api/Dtos.cs` and `Dashboard/Services/CollectorClient.cs`
      are still in sync — the JSON contract is the boundary, there is no shared assembly
- [ ] If persistence changed: `PersistedSession.From()` and `ToSession()` both updated
