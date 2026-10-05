using LocalGhost.Dashboard.Services;
using LocalGhost.Shared.Models;

namespace LocalGhost.Dashboard.Api;

/// <summary>
/// Minimal API endpoints the Agent POSTs deploy events to.
/// Dashboard then broadcasts via SignalR to all browser clients.
/// </summary>
public static class DeployEventEndpoint
{
    public static void MapDeployEvents(this WebApplication app)
    {
        var api = app.MapGroup("/api/deploy");

        api.MapGet("/jobs", async (HttpContext context, int? max, AgentRequestAuthorizer auth,
            ProjectSourceService source, CancellationToken cancellationToken) =>
        {
            if (!auth.IsAllowed(context)) return Results.Unauthorized();
            return Results.Ok(await source.ClaimJobsAsync(max ?? 2, cancellationToken));
        });

        // POST /api/deploy/start  — called when deploy begins
        api.MapPost("/start", async (HttpContext context, DeployRecord record,
            DeployStateService state, AgentRequestAuthorizer auth) =>
        {
            if (!auth.IsAllowed(context)) return Results.Unauthorized();
            await state.StartDeployAsync(record);
            return Results.Ok();
        });

        // POST /api/deploy/log   — called for each log line
        api.MapPost("/log", async (HttpContext context, LogEntry entry,
            DeployStateService state, AgentRequestAuthorizer auth) =>
        {
            if (!auth.IsAllowed(context)) return Results.Unauthorized();
            await state.AddLogAsync(entry);
            return Results.Ok();
        });

        api.MapPost("/progress", async (HttpContext context, DeployRecord record,
            DeployStateService state, AgentRequestAuthorizer auth) =>
        {
            if (!auth.IsAllowed(context)) return Results.Unauthorized();
            await state.UpdateDeployAsync(record);
            return Results.Ok();
        });

        api.MapPost("/heartbeat", (HttpContext context, AgentHeartbeat heartbeat,
            DeployStateService state, AgentRequestAuthorizer auth) =>
        {
            if (!auth.IsAllowed(context)) return Results.Unauthorized();
            state.ReportAgentHeartbeat(heartbeat);
            return Results.Ok();
        });

        // POST /api/deploy/finish — called when deploy completes or fails
        api.MapPost("/finish", async (HttpContext context, DeployRecord record,
            DeployStateService state, ProjectNotificationService notifications, AgentRequestAuthorizer auth) =>
        {
            if (!auth.IsAllowed(context)) return Results.Unauthorized();
            await state.FinishDeployAsync(record);
            await notifications.NotifyAsync(record);
            return Results.Ok();
        });

        // GET /api/deploy/history — Agent can verify connection
        api.MapGet("/ping", (HttpContext context, AgentRequestAuthorizer auth) =>
            auth.IsAllowed(context)
                ? Results.Ok(new { status = "LocalGhost Dashboard online" })
                : Results.Unauthorized());

        api.MapGet("/status", (HttpContext context, DeployStateService state,
            AgentRequestAuthorizer auth) =>
            auth.IsAllowed(context)
                ? Results.Ok(new
                {
                    dashboard = "online",
                    agentOnline = state.AgentOnline,
                    lastAgentHeartbeat = state.LastAgentHeartbeat,
                    agentVersion = state.AgentVersion,
                    activeJobs = state.AgentActiveJobs
                })
                : Results.Unauthorized());
    }
}
