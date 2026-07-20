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
// Port defaults to 4318 (standard OTLP/HTTP). Override via appsettings or
// environment variable when another process already holds 4318:
//   appsettings.Development.json → "CopilotScope": { "CollectorPort": 4319 }
//   env var                      → CopilotScope__CollectorPort=4319
// Keep VS Code setting in sync: "github.copilot.chat.otel.otlpEndpoint": "http://localhost:<port>"
var collectorPort = builder.Configuration.GetValue<int>("CopilotScope:CollectorPort", 4318);

var collector = builder.AddProject<Projects.CopilotScope_Collector>("collector")
    .WithReference(db)
    .WaitFor(db)
    .WithEndpoint("http", e =>
    {
        e.Port = collectorPort;
        e.TargetPort = collectorPort;
        e.IsProxied = false;
    });

// ----------------------------------------------------------------- dashboard
builder.AddProject<Projects.CopilotScope_Dashboard>("dashboard")
    .WithReference(collector)
    .WaitFor(collector)
    .WithExternalHttpEndpoints();

builder.Build().Run();
