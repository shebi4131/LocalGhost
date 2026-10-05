using LocalGhost.Dashboard.Data;
using LocalGhost.Dashboard.Hubs;
using LocalGhost.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LocalGhost.Dashboard.Services;

public sealed class DeployStateService(
    IHubContext<DeployHub> hub,
    ILogger<DeployStateService> logger,
    IDbContextFactory<ProjectDbContext> dbFactory)
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, DeployRecord> _currentByProject = new();
    private readonly Dictionary<Guid, List<LogEntry>> _logsByProject = new();

    public DeployRecord? CurrentDeploy { get; private set; }
    private readonly List<DeployRecord> _history = [];
    private readonly List<LogEntry> _currentLogs = [];
    private DateTime? _lastAgentHeartbeat;
    private string _agentVersion = string.Empty;
    private int _agentActiveJobs;
    public IReadOnlyList<DeployRecord> History { get { lock (_sync) return _history.ToList(); } }
    public IReadOnlyList<LogEntry> CurrentLogs { get { lock (_sync) return _currentLogs.ToList(); } }
    public bool IsDeploying { get { lock (_sync) return _currentByProject.Count > 0; } }
    public event Action? OnChange;
    public bool AgentOnline { get { lock (_sync) return _lastAgentHeartbeat is { } value && DateTime.UtcNow - value < TimeSpan.FromSeconds(30); } }
    public DateTime? LastAgentHeartbeat { get { lock (_sync) return _lastAgentHeartbeat; } }
    public string AgentVersion { get { lock (_sync) return _agentVersion; } }
    public int AgentActiveJobs { get { lock (_sync) return _agentActiveJobs; } }

    public void ReportAgentHeartbeat(AgentHeartbeat heartbeat)
    {
        lock (_sync)
        {
            _lastAgentHeartbeat = DateTime.UtcNow;
            _agentVersion = heartbeat.Version;
            _agentActiveJobs = heartbeat.ActiveJobs;
        }
        NotifyStateChanged();
    }

    public async Task StartDeployAsync(DeployRecord record)
    {
        lock (_sync)
        {
            CurrentDeploy = record;
            _currentByProject[record.ProjectId] = record;
            _logsByProject[record.ProjectId] = [];
            _currentLogs.Clear();
            _history.Insert(0, record);
            if (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.DeploymentRuns.AnyAsync(x => x.Id == record.Id))
        {
            db.DeploymentRuns.Add(ToEntity(record));
            await db.SaveChangesAsync();
        }

        NotifyStateChanged();
        await hub.Clients.Groups("dashboard", $"project:{record.ProjectId}").SendAsync("DeployStarted", record);
        logger.LogInformation("Deploy started for {Project}: {Sha}", record.ProjectName, record.ShortSha);
    }

    public async Task AddLogAsync(LogEntry entry)
    {
        lock (_sync)
        {
            _currentLogs.Add(entry);
            if (_currentLogs.Count > 500) _currentLogs.RemoveAt(0);
            if (!_logsByProject.TryGetValue(entry.ProjectId, out var logs))
                _logsByProject[entry.ProjectId] = logs = [];
            logs.Add(entry);
            if (logs.Count > 500) logs.RemoveAt(0);
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        db.DeploymentLogs.Add(new DeploymentLogEntity
        {
            Id = entry.Id, ProjectId = entry.ProjectId, DeployId = entry.DeployId,
            Timestamp = entry.Timestamp, Message = entry.Message, Level = (int)entry.Level, Source = entry.Source
        });
        await db.SaveChangesAsync();

        NotifyStateChanged();
        await hub.Clients.Groups("dashboard", $"project:{entry.ProjectId}").SendAsync("LogReceived", entry);
    }

    public async Task UpdateDeployAsync(DeployRecord record)
    {
        lock (_sync)
        {
            CurrentDeploy = record;
            _currentByProject[record.ProjectId] = record;
            var existing = _history.FirstOrDefault(x => x.Id == record.Id);
            if (existing is not null) _history[_history.IndexOf(existing)] = record;
        }

        NotifyStateChanged();
        await hub.Clients.Groups("dashboard", $"project:{record.ProjectId}").SendAsync("DeployProgress", record);
    }

    public async Task FinishDeployAsync(DeployRecord record)
    {
        lock (_sync)
        {
            CurrentDeploy = record;
            _currentByProject.Remove(record.ProjectId);
            var existing = _history.FirstOrDefault(x => x.Id == record.Id);
            if (existing is not null) _history[_history.IndexOf(existing)] = record;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await db.DeploymentRuns.FindAsync(record.Id);
        if (run is null) db.DeploymentRuns.Add(ToEntity(record));
        else Apply(run, record);

        var project = await db.Projects.FindAsync(record.ProjectId);
        if (project is not null)
        {
            project.ManualDeployRequested = false;
            project.IsDeploymentInProgress = false;
            project.AgentLeaseUntil = null;
            project.UpdatedAt = DateTime.UtcNow;
            if (record.Status == DeployStatus.Success) project.LastSuccessfulCommitSha = record.CommitSha;
        }
        await db.SaveChangesAsync();

        NotifyStateChanged();
        await hub.Clients.Groups("dashboard", $"project:{record.ProjectId}").SendAsync("DeployFinished", record);
        logger.LogInformation("Deploy finished for {Project}: {Sha} — {Status}", record.ProjectName, record.ShortSha, record.Status);
    }

    public DeployRecord? GetCurrent(Guid projectId)
    {
        lock (_sync) return _currentByProject.GetValueOrDefault(projectId);
    }

    public IReadOnlyList<LogEntry> GetCurrentLogs(Guid projectId)
    {
        lock (_sync) return _logsByProject.TryGetValue(projectId, out var logs) ? logs.ToList() : [];
    }

    public string StatusText { get { lock (_sync) return _currentByProject.Count > 0 ? $"Deploying {_currentByProject.Count} project(s)" : "Idle"; } }
    public string StatusBadgeClass => IsDeploying ? "badge-running" : "badge-success";
    public string LastDeployTime { get { lock (_sync) return _history.FirstOrDefault()?.StartedAt.ToLocalTime().ToString("HH:mm  dd MMM") ?? "Never"; } }
    public string LastSha { get { lock (_sync) return _history.FirstOrDefault()?.ShortSha ?? "—"; } }
    public int TotalCount { get { lock (_sync) return _history.Count; } }
    public int SuccessRate { get { lock (_sync) return _history.Count == 0 ? 0 : (int)((double)_history.Count(x => x.Status == DeployStatus.Success) / _history.Count * 100); } }

    private void NotifyStateChanged() => OnChange?.Invoke();

    private static DeploymentRunEntity ToEntity(DeployRecord record)
    {
        var entity = new DeploymentRunEntity { Id = record.Id };
        Apply(entity, record);
        return entity;
    }

    private static void Apply(DeploymentRunEntity entity, DeployRecord record)
    {
        entity.ProjectId = record.ProjectId;
        entity.ProjectName = record.ProjectName;
        entity.CommitSha = record.CommitSha;
        entity.CommitMessage = record.CommitMessage;
        entity.CommitAuthor = record.CommitAuthor;
        entity.Branch = record.Branch;
        entity.CommittedAt = record.CommittedAt;
        entity.Status = (int)record.Status;
        entity.StartedAt = record.StartedAt;
        entity.FinishedAt = record.FinishedAt;
        entity.FullLog = record.FullLog;
        entity.ErrorMessage = record.ErrorMessage;
        entity.FailedStep = record.FailedStep;
    }
}
