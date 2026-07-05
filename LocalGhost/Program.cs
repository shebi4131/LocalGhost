using LocalGhost.Services;
using LocalGhost.Shared.Models;

var builder = Host.CreateApplicationBuilder(args);

// ── Config ─────────────────────────────────────────────────────
builder.Services.Configure<AppSettings>(
    builder.Configuration.GetSection("LocalGhost"));

// ── Services ───────────────────────────────────────────────────
builder.Services.AddSingleton<GitPoller>();   // ← add this

// ── Worker ─────────────────────────────────────────────────────
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();