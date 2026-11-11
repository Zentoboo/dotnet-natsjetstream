using NatsSyncLab.BuildingBlocks.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNatsJetStream(builder.Configuration);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "backend-b" }));

app.Run();
