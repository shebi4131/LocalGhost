using LocalGhost.Shared.Models;
using System.Net.Http.Json;

namespace LocalGhost.Agent.Services;

/// <summary>
/// Sends deploy events from the Agent to the Dashboard via HTTP.
/// Dashboard then broadcasts via SignalR to all browsers.
/// </summary>
public class AgentEventSender
{
    private readonly HttpClient _http;
    private readonly ILogger<AgentEventSender> _logger;
    private readonly string _dashboardUrl;

    public AgentEventSender(IConfiguration config, ILogger<AgentEventSender> logger)
    {
        _logger = logger;
        _dashboardUrl = config["DashboardUrl"] ?? "http://localhost:5050";
        _http = new HttpClient
        {
            BaseAddress = new Uri(_dashboardUrl),
            Timeout = Timeout.InfiniteTimeSpan
        };
        var apiKey = config["AgentApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Add("X-LocalGhost-Agent-Key", apiKey);
    }

    public async Task<List<ProjectDeploymentJob>> GetJobsAsync(int maxJobs, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            return await _http.GetFromJsonAsync<List<ProjectDeploymentJob>>($"/api/deploy/jobs?max={maxJobs}", timeout.Token) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Dashboard job feed unavailable: {Msg}", ex.Message);
            return [];
        }
    }

    public async Task SendStartAsync(DeployRecord record)
        => await PostAsync("/api/deploy/start", record);

    public async Task SendLogAsync(LogEntry entry)
        => await PostAsync("/api/deploy/log", entry);

    public async Task SendProgressAsync(DeployRecord record)
        => await PostAsync("/api/deploy/progress", record);

    public async Task SendHeartbeatAsync(AgentHeartbeat heartbeat)
        => await PostAsync("/api/deploy/heartbeat", heartbeat);

    public async Task SendFinishAsync(DeployRecord record)
        => await PostAsync("/api/deploy/finish", record);

    private async Task PostAsync<T>(string path, T data)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await _http.PostAsJsonAsync(path, data, timeout.Token);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            // Never crash the deploy because dashboard is offline
            _logger.LogWarning("Dashboard unreachable ({Path}): {Msg}", path, ex.Message);
        }
    }
}
