using LocalGhost;
using LocalGhost.Shared.Models;

var builder = Host.CreateApplicationBuilder(args);

// ── Read LocalGhost config section into AppSettings ────────────
builder.Services.Configure<AppSettings>(
    builder.Configuration.GetSection("LocalGhost"));

// ── Register the background worker ─────────────────────────────
builder.Services.AddHostedService<Worker>();

// ── Allows running as a Windows Service (Day 7) ─────────────────
// Uncomment this line when you install on the server:
// builder.Services.AddWindowsService(o => o.ServiceName = "LocalGhostAgent");

var host = builder.Build();
host.Run();