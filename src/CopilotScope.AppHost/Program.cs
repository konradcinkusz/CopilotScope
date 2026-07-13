var builder = DistributedApplication.CreateBuilder(args);

// ------------------------------------------------------------------ database
// Postgres in a container; the named volume keeps data across restarts.
// pgAdmin runs as a sibling container and shows up as a resource in the
// Aspire dashboard — open it from there to browse the sessions table.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("copilotscope-pgdata")
    .WithPgAdmin();

var db = postgres.AddDatabase("copilotdb");

// ----------------------------------------------------------------- collector
// Fixed, unproxied port 4318 so VS Code / Copilot CLI always target
// http://localhost:4318, regardless of Aspire's random port allocation.
var collector = builder.AddProject<Projects.CopilotScope_Collector>("collector")
    .WithReference(db)
    .WaitFor(db)
    .WithEndpoint("http", e =>
    {
        e.Port = 4318;
        e.TargetPort = 4318;
        e.IsProxied = false;
    });

// ----------------------------------------------------------------- dashboard
builder.AddProject<Projects.CopilotScope_Dashboard>("dashboard")
    .WithReference(collector)
    .WaitFor(collector)
    .WithExternalHttpEndpoints();

builder.Build().Run();
