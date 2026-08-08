using CopilotScope.AgentForge.Domain;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotScope.Tests;

// Regression guard: Microsoft.Extensions.Configuration.Binder double-binds List<T> properties on
// types that also have a matching parameterized constructor (records included) — it runs both
// the constructor-matching bind AND a post-construction property bind, appending every list item
// twice. PersonaCohort was originally a record and silently produced SessionIds = ["s-1", "s-1"]
// for a single configured id. It must stay a plain settable class (see Domain/PersonaCohort.cs).
public class CohortsOptionsBindingTests
{
    [Fact]
    public void BindingASingleSessionIdDoesNotDuplicateIt()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cohorts:0:PersonaId"] = "demo-persona",
                ["Cohorts:0:DisplayLabel"] = "Demo Persona",
                ["Cohorts:0:ConsentGrantedBy"] = "local-dev",
                ["Cohorts:0:ConsentDate"] = "2026-08-08",
                ["Cohorts:0:SessionIds:0"] = "seed-demo-1"
            })
            .Build();

        var cohorts = new List<PersonaCohort>();
        config.GetSection("Cohorts").Bind(cohorts);

        var cohort = Assert.Single(cohorts);
        Assert.Equal("demo-persona", cohort.PersonaId);
        Assert.Equal(["seed-demo-1"], cohort.SessionIds);
    }
}
