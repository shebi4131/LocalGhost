using LocalGhost.Dashboard.Data;
using LocalGhost.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Octokit;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace LocalGhost.Dashboard.Services;

public sealed class ProjectSourceService(
    IDbContextFactory<ProjectDbContext> dbFactory,
    GitHubCredentialService githubCredentials,
    DeployStateService state,
    ProjectNotificationService notifications,
    UserNotificationService userNotifications,
    ProjectPreflightService preflight,
    IDataProtectionProvider dataProtection,
    ILogger<ProjectSourceService> logger)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("LocalGhost.ProjectSecrets.v1");
    private readonly SemaphoreSlim _claimGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, string> _manualBranches = new();

    public void QueueManualBranch(Guid projectId, string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) _manualBranches.TryRemove(projectId, out _);
        else _manualBranches[projectId] = branch.Trim();
    }

    public async Task<List<ProjectDeploymentJob>> ClaimJobsAsync(int maxJobs, CancellationToken cancellationToken)
    {
        await _claimGate.WaitAsync(cancellationToken);
        try
        {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var candidates = await db.Projects
            .Where(x => x.IsActive && !x.IsArchived &&
                (!x.IsDeploymentInProgress || x.AgentLeaseUntil < now) &&
                (x.ManualDeployRequested || x.LastPolledAt == null || x.LastPolledAt <= now.AddSeconds(-x.PollIntervalSeconds)))
            .OrderBy(x => x.LastPolledAt)
            .Take(Math.Clamp(maxJobs, 1, 8))
            .ToListAsync(cancellationToken);

        var jobs = new List<ProjectDeploymentJob>();
        var pendingAlerts = new List<(Guid ProjectId, string EventKey, string Kind, string Title, string Message, string Url, ProjectMemberRole MinimumRole)>();
        foreach (var project in candidates)
        {
            project.LastPolledAt = now;
            DeployRecord? record = null;
            try
            {
                var token = await githubCredentials.GetProjectTokenAsync(project, cancellationToken);
                _manualBranches.TryRemove(project.Id, out var requestedBranch);
                var approved = project.RequiresApproval
                    ? await db.DeploymentApprovals.Where(x => x.ProjectId == project.Id && x.Approved == true && x.ConsumedAt == null)
                        .OrderBy(x => x.RequestedAt).FirstOrDefaultAsync(cancellationToken)
                    : null;
                var commit = approved is null
                    ? await GetPendingCommitAsync(project, token, db, project.ManualDeployRequested, requestedBranch, cancellationToken)
                    : await GetCommitAsync(project, token, approved.Branch, approved.CommitSha);
                if (commit is null) continue;
                if (!project.ManualDeployRequested && approved is null)
                {
                    var headline = commit.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault()?.Trim() ?? string.Empty;
                    if (headline.Length > 120) headline = headline[..120] + "…";
                    pendingAlerts.Add((project.Id, $"commit:{project.Id}:{commit.Branch}:{commit.Sha}", "commit",
                        $"New commit on {commit.Branch}", $"{project.Name} · {commit.ShortSha} · {headline}",
                        $"/projects/{project.Id}", ProjectMemberRole.Viewer));
                }
                if (project.RequiresApproval && approved is null)
                {
                    var prior = await db.DeploymentApprovals.FirstOrDefaultAsync(x =>
                        x.ProjectId == project.Id && x.Branch == commit.Branch && x.CommitSha == commit.Sha, cancellationToken);
                    if (project.ManualDeployRequested && prior is { Approved: true })
                    {
                        // Approval is tied to this exact SHA. Reuse it only for an explicit retry,
                        // never for the automatic poll after a failed deployment.
                        approved = prior;
                    }
                    else
                    {
                        if (prior is null)
                        {
                            var request = new DeploymentApprovalEntity
                            {
                                ProjectId = project.Id, Branch = commit.Branch, CommitSha = commit.Sha,
                                RequestedByUserId = project.OwnerUserId
                            };
                            db.DeploymentApprovals.Add(request);
                            pendingAlerts.Add((project.Id, $"approval:{request.Id}", "approval", "Approval needed",
                                $"{project.Name} · {project.Environment} · {commit.Branch} · {commit.ShortSha}",
                                "/approvals", ProjectMemberRole.Manager));
                        }
                        else if (project.ManualDeployRequested && prior.Approved == false)
                        {
                            prior.Approved = null;
                            prior.DecidedAt = null;
                            prior.DecidedByUserId = null;
                            prior.ConsumedAt = null;
                        }
                        project.ManualDeployRequested = false;
                        continue;
                    }
                }
                if (approved is not null) approved.ConsumedAt = now;

                record = new DeployRecord
                {
                    ProjectId = project.Id,
                    ProjectName = project.Name,
                    CommitSha = commit.Sha,
                    CommitMessage = commit.Message,
                    CommitAuthor = commit.Author,
                    Branch = commit.Branch,
                    CommittedAt = commit.CommittedAt,
                    Status = DeployStatus.Running,
                    Stage = PipelineStage.Source,
                    StageMessage = "Synchronizing source from GitHub",
                    StartedAt = DateTime.UtcNow
                };
                await state.StartDeployAsync(record);
                await userNotifications.PublishProjectAsync(project.Id, $"run-start:{record.Id}", "running",
                    "Deployment started", $"{project.Name} · {commit.Branch} · {commit.ShortSha}", $"/projects/{project.Id}");
                var preflightReport = await preflight.RunAsync(project.OwnerUserId, project.Id, cancellationToken);
                if (preflightReport is null || !preflightReport.CanDeploy)
                {
                    var failure = preflightReport?.Checks.FirstOrDefault(x => x.Status == LocalGhost.Dashboard.Models.PreflightCheckStatus.Failed);
                    throw new InvalidOperationException(failure is null
                        ? "Project preflight could not be completed."
                        : $"Preflight failed: {failure.Name} — {failure.Detail}");
                }
                await AddLogAsync(record, $"✓ Preflight passed ({preflightReport.PassedCount} checks, {preflightReport.WarningCount} warnings)", LocalGhost.Shared.Models.LogLevel.Success);
                await AddLogAsync(record, $"◆ New commit detected on {commit.Branch}: {commit.ShortSha}", LocalGhost.Shared.Models.LogLevel.Success);
                await AddLogAsync(record, $"Commit message: {commit.Message.Replace('\r', ' ').Replace('\n', ' ')}");
                await AddLogAsync(record, $"▶ Synchronizing {project.RepoOwner}/{project.RepoName} into {project.SourceCodePath}");
                var sourceResult = await SyncSourceAsync(project, token, commit.Branch, commit.Sha, cancellationToken);
                await AddLogAsync(record, $"✓ Source synchronized{(string.IsNullOrWhiteSpace(sourceResult) ? string.Empty : $": {sourceResult}")}", LocalGhost.Shared.Models.LogLevel.Success);
                project.IsDeploymentInProgress = true;
                project.AgentLeaseUntil = now.AddMinutes(Math.Max(15, project.BuildTimeoutMinutes + 10));
                project.ManualDeployRequested = false;
                jobs.Add(new ProjectDeploymentJob
                {
                    DeploymentId = record.Id,
                    StartedAt = record.StartedAt,
                    Project = ToSafeConfiguration(project, commit.Branch),
                    Commit = commit
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not prepare deployment job for project {Project}", project.Name);
                var sourceError = DescribeSourceError(ex, project);
                record ??= new DeployRecord
                {
                    ProjectId = project.Id,
                    ProjectName = project.Name,
                    CommitMessage = "Source preparation failed",
                    CommitAuthor = "LocalGhost",
                    Branch = project.Branch,
                    Stage = PipelineStage.Source,
                    StartedAt = DateTime.UtcNow
                };
                if (state.GetCurrent(project.Id)?.Id != record.Id) await state.StartDeployAsync(record);
                record.Status = DeployStatus.Failed;
                record.StageMessage = "Source preparation failed";
                record.ErrorMessage = sourceError;
                record.FailedStep = "Source";
                record.FinishedAt = DateTime.UtcNow;
                await AddLogAsync(record, $"✖ Source preparation failed: {sourceError}", LocalGhost.Shared.Models.LogLevel.Error);
                await state.FinishDeployAsync(record);
                await notifications.NotifyAsync(record);
                project.ManualDeployRequested = false;
                project.IsDeploymentInProgress = false;
                project.AgentLeaseUntil = null;
                // Avoid filling history every few seconds for the same configuration error.
                // A manual deployment still bypasses this cooldown after the user fixes settings.
                project.LastPolledAt = now.AddMinutes(5);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var alert in pendingAlerts)
            await userNotifications.PublishProjectAsync(alert.ProjectId, alert.EventKey, alert.Kind,
                alert.Title, alert.Message, alert.Url, alert.MinimumRole);
        return jobs;
        }
        finally
        {
            _claimGate.Release();
        }
    }

    private static async Task<CommitDescriptor?> GetPendingCommitAsync(ProjectEntity project, string token,
        ProjectDbContext db, bool force, string? requestedBranch, CancellationToken cancellationToken)
    {
        var github = new GitHubClient(new ProductHeaderValue("LocalGhost-Dashboard"))
        {
            Credentials = new Credentials(token)
        };

        var branches = ParseBranches(project.Branch);
        if (force)
        {
            var branch = branches.Contains(requestedBranch ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                ? requestedBranch!
                : branches[0];
            return await GetLatestCommitAsync(github, project, branch);
        }

        foreach (var branch in branches)
        {
            var commit = await GetLatestCommitAsync(github, project, branch);
            var baseline = await db.ProjectBranchBaselines.FirstOrDefaultAsync(x =>
                x.ProjectId == project.Id && x.Branch == branch.ToLower(), cancellationToken);
            if (baseline is not null && string.Equals(baseline.CommitSha, commit.Sha, StringComparison.OrdinalIgnoreCase))
                continue;
            var alreadyAttempted = await db.DeploymentRuns.AsNoTracking().AnyAsync(x =>
                x.ProjectId == project.Id && x.Branch == branch && x.CommitSha == commit.Sha,
                cancellationToken);
            if (!alreadyAttempted) return commit;
        }

        return null;
    }

    private static async Task<CommitDescriptor> GetLatestCommitAsync(GitHubClient github,
        ProjectEntity project, string branch)
    {
        var commits = await github.Repository.Commit.GetAll(project.RepoOwner, project.RepoName,
            new CommitRequest { Sha = branch });
        if (commits.Count == 0) throw new InvalidOperationException($"No commits were found on branch '{branch}'.");
        var latest = commits[0];
        return new CommitDescriptor
        {
            Sha = latest.Sha,
            Author = latest.Commit.Author?.Name ?? latest.Author?.Login ?? "Unknown",
            Message = latest.Commit.Message,
            Branch = branch,
            CommittedAt = latest.Commit.Author?.Date.UtcDateTime ?? DateTime.UtcNow
        };
    }

    private static async Task<CommitDescriptor> GetCommitAsync(ProjectEntity project, string token, string branch, string sha)
    {
        var github = new GitHubClient(new ProductHeaderValue("LocalGhost-Dashboard"))
        { Credentials = new Credentials(token) };
        var item = await github.Repository.Commit.Get(project.RepoOwner, project.RepoName, sha);
        return new CommitDescriptor
        {
            Sha = item.Sha, Branch = branch,
            Author = item.Commit.Author?.Name ?? item.Author?.Login ?? "Unknown",
            Message = item.Commit.Message,
            CommittedAt = item.Commit.Author?.Date.UtcDateTime ?? DateTime.UtcNow
        };
    }

    private async Task<string> SyncSourceAsync(ProjectEntity project, string token, string branch, string sha,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(project.SourceCodePath);
        var isRepository = Directory.Exists(Path.Combine(project.SourceCodePath, ".git"));
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));

        if (!isRepository)
        {
            await RunGitAsync(project.SourceCodePath,
                $"clone --branch {Quote(branch)} --single-branch https://github.com/{project.RepoOwner}/{project.RepoName}.git .",
                basic, cancellationToken);
            return await RunGitAsync(project.SourceCodePath, $"checkout --detach {Quote(sha)}", basic, cancellationToken);
        }

        // Old checkouts may have stored a PAT in origin. Keep the remote credential-free;
        // the short-lived credential is supplied only to this Git process.
        await RunGitAsync(project.SourceCodePath,
            $"remote set-url origin {Quote($"https://github.com/{project.RepoOwner}/{project.RepoName}.git")}",
            basic, cancellationToken);
        await RunGitAsync(project.SourceCodePath, $"fetch origin {Quote(branch)}", basic, cancellationToken);
        return await RunGitAsync(project.SourceCodePath,
            $"checkout --detach {Quote(sha)}", basic, cancellationToken);
    }

    private async Task<string> RunGitAsync(string workingDirectory, string arguments, string basic,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
        startInfo.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
        startInfo.Environment["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {basic}";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git synchronization failed: {LastLine(error)}");
        return LastLine(output);
    }

    private Task AddLogAsync(DeployRecord record, string message,
        LocalGhost.Shared.Models.LogLevel level = LocalGhost.Shared.Models.LogLevel.Info) => state.AddLogAsync(new LogEntry
        {
            ProjectId = record.ProjectId,
            DeployId = record.Id,
            Timestamp = DateTime.UtcNow,
            Message = message,
            Level = level,
            Source = "Dashboard"
        });

    private ProjectConfiguration ToSafeConfiguration(ProjectEntity x, string branch) => new()
    {
        Id = x.Id, Kind = x.Kind, Name = x.Name, Description = x.Description, Environment = x.Environment,
        IsActive = x.IsActive, ForceDeploy = x.ManualDeployRequested, LastSuccessfulCommitSha = x.LastSuccessfulCommitSha,
        PollIntervalSeconds = x.PollIntervalSeconds,
        GitHub = new GitHubSettings { RepoOwner = x.RepoOwner, RepoName = x.RepoName, Branch = branch },
        Build = new BuildSettings { SourceCodePath = x.SourceCodePath, ApiProjectPath = x.ApiProjectPath, ReactProjectPath = x.ReactProjectPath, ApiOutputPath = x.ApiOutputPath, ReactOutputPath = x.ReactOutputPath, TimeoutMinutes = x.BuildTimeoutMinutes },
        IIS = new IISSettings { SitePath = x.SitePath, ReactSitePath = x.ReactSitePath, AppPoolName = x.AppPoolName, BackupPath = x.BackupPath, HealthCheckUrl = x.HealthCheckUrl },
        Migration = new MigrationSettings { Enabled = x.MigrationsEnabled, ProjectPath = x.MigrationProjectPath,
            StartupProjectPath = x.MigrationStartupProjectPath, DbContextName = x.MigrationDbContextName,
            ConnectionName = x.MigrationConnectionName, SqlBackupPath = x.SqlBackupPath, TimeoutMinutes = x.MigrationTimeoutMinutes,
            ConnectionString = x.MigrationsEnabled ? _protector.Unprotect(x.DatabaseConnectionProtected) : string.Empty },
        Smtp = new SmtpSettings { Enabled = x.EmailEnabled, Host = x.SmtpHost, Port = x.SmtpPort, Username = x.SmtpUsername, FromAddress = x.SmtpFromAddress, ToAddress = x.SmtpToAddress, EnableSsl = x.SmtpEnableSsl, NotifyOnSuccess = x.NotifyOnSuccess, NotifyOnFailure = x.NotifyOnFailure }
    };

    private static string DescribeSourceError(Exception error, ProjectEntity project) => error switch
    {
        NotFoundException => $"GitHub could not access {project.RepoOwner}/{project.RepoName} on branch {project.Branch}. Check the owner, repository and branch, then grant the fine-grained token access to this repository with Contents: Read permission.",
        AuthorizationException => "GitHub rejected the saved token. Create or update a token with access to this repository, then save it in project settings.",
        _ => error.Message
    };
    public static IReadOnlyList<string> ParseBranches(string value) => value
        .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .DefaultIfEmpty("main")
        .ToList();
    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
    private static string LastLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
}
