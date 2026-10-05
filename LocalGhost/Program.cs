using LocalGhost.Agent.Services;
using LocalGhost.Services;
using Microsoft.Extensions.Hosting.WindowsServices;
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "LocalGhost Agent"); // ← uncomment this

builder.Services.AddSingleton<BuildRunner>();
builder.Services.AddSingleton<IISDeployer>();
builder.Services.AddSingleton<AgentEventSender>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
