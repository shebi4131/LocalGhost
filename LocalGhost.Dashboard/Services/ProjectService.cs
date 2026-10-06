using LocalGhost.Dashboard.Data;
using LocalGhost.Dashboard.Models;
using LocalGhost.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Octokit;

namespace LocalGhost.Dashboard.Services;

public sealed class ProjectService(
    IDbContextFactory<ProjectDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    GitHubCredentialService githubCredentials,
    ProjectAccessService access)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("LocalGhost.ProjectSecrets.v1");

    public async Task<List<ProjectSummary>> GetProjectsAsync(string ownerUserId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var projects = await db.Projects.AsNoTracking()
            .Where(x => !x.IsArchived && (x.OwnerUserId == ownerUserId ||
                db.ProjectMembers.Any(m => m.ProjectGroupId == x.ProjectGroupId && m.UserId == ownerUserId)))
            .OrderByDescending(x => x.UpdatedAt)
            .ToListAsync();
        var summaries = projects.Select(ToSummary).ToList();
        var ids = summaries.Select(x => x.Id).ToList();
        var runs = await db.DeploymentRuns.AsNoTracking().Where(x => ids.Contains(x.ProjectId) && !x.IsHidden)
            .OrderByDescending(x => x.StartedAt).ToListAsync();
        foreach (var summary in summaries)
        {
            var projectRuns = runs.Where(x => x.ProjectId == summary.Id).ToList();
            var last = projectRuns.FirstOrDefault();
            summary.LastDeploymentStatus = last is null ? null : (DeployStatus)last.Status;
            summary.LastDeploymentAt = last?.StartedAt;
            summary.DeploymentCount = projectRuns.Count;
            summary.SuccessRate = projectRuns.Count == 0 ? 0 : (int)((double)projectRuns.Count(x => x.Status == (int)DeployStatus.Success) / projectRuns.Count * 100);
            var lastSuccess = projectRuns.FirstOrDefault(x => x.Status == (int)DeployStatus.Success);
            summary.LastSuccessfulBranch = lastSuccess?.Branch;
            summary.LastSuccessfulCommitMessage = lastSuccess?.CommitMessage;
            summary.LastSuccessfulCommittedAt = lastSuccess?.CommittedAt;
            summary.LastSuccessfulDeploymentAt = lastSuccess?.StartedAt;
        }
        return summaries;
    }

    public async Task<ProjectSummary?> GetProjectAsync(string ownerUserId, Guid projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == projectId && !x.IsArchived &&
                (x.OwnerUserId == ownerUserId || db.ProjectMembers.Any(m => m.ProjectGroupId == x.ProjectGroupId && m.UserId == ownerUserId)));
        return project is null ? null : ToSummary(project);
    }

    public async Task<Guid> CreateAsync(string ownerUserId, ProjectEditModel model)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await VerifySourceAsync(ownerUserId, model, null);
        var project = new ProjectEntity { OwnerUserId = ownerUserId };
        project.ProjectGroupId = project.Id;
        Apply(project, model, isNew: true);
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    public async Task<bool> UpdateAsync(string ownerUserId, Guid projectId, ProjectEditModel model)
    {
        if (!await access.CanAsync(ownerUserId, projectId, ProjectMemberRole.Manager)) return false;
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId && !x.IsArchived);
        if (project is null) return false;
        var otherTargets = await db.Projects.AsNoTracking().Where(x => x.ProjectGroupId == project.ProjectGroupId && x.Id != projectId && !x.IsArchived).ToListAsync();
        if (otherTargets.Any(x => string.Equals(x.Environment, model.Environment.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("That environment name is already used by this project.");
        if (otherTargets.Any(x => SamePath(x.SitePath, model.SitePath) || SamePath(x.ReactSitePath, model.SitePath) ||
            SamePath(x.SitePath, model.ReactSitePath) || SamePath(x.ReactSitePath, model.ReactSitePath)))
            throw new InvalidOperationException("This IIS destination is already used by another environment in the project.");
        if (otherTargets.Any(x => SamePath(x.SourceCodePath, model.SourceCodePath) || SamePath(x.ApiOutputPath, model.ApiOutputPath) ||
            SamePath(x.ReactOutputPath, model.ReactOutputPath)))
            throw new InvalidOperationException("Source checkout and build output folders must be separate for each environment.");
        await VerifySourceAsync(ownerUserId, model, project);

        var oldBranches = ProjectSourceService.ParseBranches(project.Branch);
        var newBranches = ProjectSourceService.ParseBranches(model.Branch);
        var repositoryChanged = !string.Equals(project.RepoOwner, model.RepoOwner.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(project.RepoName, model.RepoName.Trim(), StringComparison.OrdinalIgnoreCase);
        var addedBranches = repositoryChanged
            ? newBranches
            : newBranches.Where(branch => !oldBranches.Contains(branch, StringComparer.OrdinalIgnoreCase)).ToList();

        // Capture the remote tip at save time; a commit pushed after this point must not be swallowed by the first poll.
        var heads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (addedBranches.Count > 0)
        {
            var token = model.GitHubAuthMode == "Local"
                ? await githubCredentials.GetTokenAsync()
                : string.IsNullOrWhiteSpace(model.GitHubToken)
                    ? _protector.Unprotect(project.GitHubTokenProtected)
                    : model.GitHubToken.Trim();
            var github = new GitHubClient(new ProductHeaderValue("LocalGhost-Dashboard"))
            {
                Credentials = new Credentials(token)
            };
            foreach (var branch in addedBranches)
            {
                try
                {
                    var commits = await github.Repository.Commit.GetAll(model.RepoOwner.Trim(), model.RepoName.Trim(),
                        new CommitRequest { Sha = branch });
                    if (commits.Count == 0) throw new InvalidOperationException("The branch has no commits.");
                    heads[branch] = commits[0].Sha;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Could not start monitoring branch '{branch}'. Check that it exists and the GitHub token has Contents: Read access.", ex);
                }
            }
        }

        var baselines = await db.ProjectBranchBaselines.Where(x => x.ProjectId == projectId).ToListAsync();
        foreach (var baseline in baselines)
        {
            if (!newBranches.Contains(baseline.Branch, StringComparer.OrdinalIgnoreCase))
                db.ProjectBranchBaselines.Remove(baseline);
            else if (heads.Remove(baseline.Branch, out var newHead))
                baseline.CommitSha = newHead;
        }
        foreach (var (branch, sha) in heads)
            db.ProjectBranchBaselines.Add(new ProjectBranchBaselineEntity
            {
                ProjectId = projectId, Branch = branch.ToLowerInvariant(), CommitSha = sha
            });

        Apply(project, model, isNew: false);
        if (addedBranches.Count > 0 || oldBranches.Count != newBranches.Count || repositoryChanged)
            project.LastPolledAt = null;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetActiveAsync(string ownerUserId, Guid projectId, bool active)
    {
        if (!await access.CanAsync(ownerUserId, projectId, ProjectMemberRole.Manager)) return false;
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId && !x.IsArchived);
        if (project is null) return false;
        project.IsActive = active;
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RequestDeploymentAsync(string ownerUserId, Guid projectId)
    {
        if (!await access.CanAsync(ownerUserId, projectId, ProjectMemberRole.Deployer)) return false;
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId && !x.IsArchived);
        if (project is null || !project.IsActive) return false;
        project.ManualDeployRequested = true;
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<DeployRecord>> GetHistoryAsync(string ownerUserId, Guid projectId, int take = 50)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await access.CanAsync(ownerUserId, projectId, ProjectMemberRole.Viewer)) return [];
        return await db.DeploymentRuns.AsNoTracking().Where(x => x.ProjectId == projectId && !x.IsHidden)
            .OrderByDescending(x => x.StartedAt).Take(take)
            .Select(x => new DeployRecord
            {
                Id = x.Id, ProjectId = x.ProjectId, ProjectName = x.ProjectName, CommitSha = x.CommitSha,
                CommitMessage = x.CommitMessage, CommitAuthor = x.CommitAuthor, Branch = x.Branch,
                Status = (DeployStatus)x.Status, StartedAt = x.StartedAt, FinishedAt = x.FinishedAt,
                FullLog = x.FullLog, ErrorMessage = x.ErrorMessage, FailedStep = x.FailedStep
            }).ToListAsync();
    }

    public async Task<List<LogEntry>> GetLogsAsync(string ownerUserId, Guid projectId, int take = 500)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await access.CanAsync(ownerUserId, projectId, ProjectMemberRole.Viewer)) return [];
        var items = await db.DeploymentLogs.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.Timestamp).Take(take).ToListAsync();
        items.Reverse();
        return items.Select(x => new LogEntry
        {
            Id = x.Id, ProjectId = x.ProjectId, DeployId = x.DeployId, Timestamp = x.Timestamp,
            Message = x.Message, Level = (LocalGhost.Shared.Models.LogLevel)x.Level, Source = x.Source
        }).ToList();
    }

    public async Task<List<LogEntry>> GetRunLogsAsync(string ownerUserId, Guid projectId, Guid runId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await access.CanAsync(ownerUserId, projectId, ProjectMemberRole.Viewer) ||
            !await db.DeploymentRuns.AnyAsync(x => x.Id == runId && x.ProjectId == projectId && !x.IsHidden)) return [];
        var items = await db.DeploymentLogs.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.DeployId == runId)
            .OrderBy(x => x.Timestamp).ToListAsync();
        return items.Select(x => new LogEntry
        {
            Id = x.Id, ProjectId = x.ProjectId, DeployId = x.DeployId, Timestamp = x.Timestamp,
            Message = x.Message, Level = (LocalGhost.Shared.Models.LogLevel)x.Level, Source = x.Source
        }).ToList();
    }

    public async Task<bool> DeleteRunAsync(string ownerUserId, Guid projectId, Guid runId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await access.CanAsync(ownerUserId, projectId, ProjectMemberRole.Manager)) return false;
        var run = await db.DeploymentRuns.FirstOrDefaultAsync(x => x.Id == runId && x.ProjectId == projectId && !x.IsHidden);
        if (run is null || run.Status is (int)DeployStatus.Running or (int)DeployStatus.Queued) return false;
        await db.DeploymentLogs.Where(x => x.ProjectId == projectId && x.DeployId == runId).ExecuteDeleteAsync();
        run.IsHidden = true;
        run.ProjectName = string.Empty;
        run.CommitMessage = string.Empty;
        run.CommitAuthor = string.Empty;
        run.FullLog = string.Empty;
        run.ErrorMessage = null;
        run.FailedStep = null;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<DeployRecord>> GetRecentHistoryAsync(string ownerUserId, int take = 12)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var projectIds = await db.Projects.AsNoTracking()
            .Where(x => !x.IsArchived && (x.OwnerUserId == ownerUserId ||
                db.ProjectMembers.Any(m => m.ProjectGroupId == x.ProjectGroupId && m.UserId == ownerUserId)))
            .Select(x => x.Id).ToListAsync();
        return await db.DeploymentRuns.AsNoTracking().Where(x => projectIds.Contains(x.ProjectId) && !x.IsHidden)
            .OrderByDescending(x => x.StartedAt).Take(take)
            .Select(x => new DeployRecord
            {
                Id = x.Id, ProjectId = x.ProjectId, ProjectName = x.ProjectName, CommitSha = x.CommitSha,
                CommitMessage = x.CommitMessage, CommitAuthor = x.CommitAuthor, Branch = x.Branch,
                Status = (DeployStatus)x.Status, StartedAt = x.StartedAt, FinishedAt = x.FinishedAt,
                FullLog = x.FullLog, ErrorMessage = x.ErrorMessage, FailedStep = x.FailedStep
            }).ToListAsync();
    }

    private void Apply(ProjectEntity target, ProjectEditModel source, bool isNew)
    {
        target.Kind = source.Kind;
        target.Name = source.Name.Trim();
        target.Description = source.Description.Trim();
        target.Environment = source.Environment.Trim();
        target.IsActive = source.IsActive;
        target.RequiresApproval = source.RequiresApproval;
        target.PollIntervalSeconds = Math.Clamp(source.PollIntervalSeconds, 10, 3600);
        target.RepoOwner = source.RepoOwner.Trim();
        target.RepoName = source.RepoName.Trim();
        target.Branch = string.Join(",", ProjectSourceService.ParseBranches(source.Branch));
        target.GitHubUseCredentialManager = source.GitHubAuthMode == "Local";
        target.GitHubRepositoryId = target.GitHubUseCredentialManager ? source.GitHubRepositoryId : null;
        if (!string.IsNullOrWhiteSpace(source.GitHubToken)) target.GitHubTokenProtected = _protector.Protect(source.GitHubToken.Trim());
        target.SourceCodePath = source.SourceCodePath.Trim();
        target.ApiProjectPath = source.ApiProjectPath.Trim();
        target.ReactProjectPath = source.Kind == ProjectKind.BlazorWebApp ? string.Empty : source.ReactProjectPath.Trim();
        target.ApiOutputPath = source.ApiOutputPath.Trim();
        target.ReactOutputPath = source.Kind == ProjectKind.BlazorWebApp ? string.Empty : source.ReactOutputPath.Trim();
        target.BuildTimeoutMinutes = Math.Clamp(source.BuildTimeoutMinutes, 1, 120);
        target.SitePath = source.SitePath.Trim();
        target.ReactSitePath = source.Kind == ProjectKind.BlazorWebApp ? string.Empty : source.ReactSitePath.Trim();
        target.AppPoolName = source.AppPoolName.Trim();
        target.BackupPath = source.BackupPath.Trim();
        target.HealthCheckUrl = source.HealthCheckUrl.Trim();
        target.MigrationsEnabled = source.MigrationsEnabled;
        target.MigrationProjectPath = source.MigrationProjectPath.Trim();
        target.MigrationStartupProjectPath = source.MigrationStartupProjectPath.Trim();
        target.MigrationDbContextName = source.MigrationDbContextName.Trim();
        target.MigrationConnectionName = source.MigrationConnectionName.Trim();
        if (!string.IsNullOrWhiteSpace(source.DatabaseConnectionString))
            target.DatabaseConnectionProtected = _protector.Protect(source.DatabaseConnectionString.Trim());
        target.SqlBackupPath = source.SqlBackupPath.Trim();
        target.MigrationTimeoutMinutes = Math.Clamp(source.MigrationTimeoutMinutes, 1, 120);
        target.EmailEnabled = source.EmailEnabled;
        target.SmtpHost = source.SmtpHost.Trim();
        target.SmtpPort = Math.Clamp(source.SmtpPort, 1, 65535);
        target.SmtpUsername = source.SmtpUsername.Trim();
        if (!string.IsNullOrWhiteSpace(source.SmtpPassword)) target.SmtpPasswordProtected = _protector.Protect(source.SmtpPassword);
        target.SmtpFromAddress = source.SmtpFromAddress.Trim();
        target.SmtpToAddress = source.SmtpToAddress.Trim();
        target.SmtpEnableSsl = source.SmtpEnableSsl;
        target.NotifyOnSuccess = source.NotifyOnSuccess;
        target.NotifyOnFailure = source.NotifyOnFailure;
        target.UpdatedAt = DateTime.UtcNow;
        if (isNew) target.CreatedAt = DateTime.UtcNow;
    }

    private static ProjectSummary ToSummary(ProjectEntity x) => new()
    {
        Id = x.Id, ProjectGroupId = x.ProjectGroupId, RequiresApproval = x.RequiresApproval,
        Kind = x.Kind, Name = x.Name, Description = x.Description, Environment = x.Environment,
        IsActive = x.IsActive, ManualDeployRequested = x.ManualDeployRequested,
        IsDeploymentInProgress = x.IsDeploymentInProgress, LastPolledAt = x.LastPolledAt,
        HasGitHubToken = !string.IsNullOrEmpty(x.GitHubTokenProtected), HasSmtpPassword = !string.IsNullOrEmpty(x.SmtpPasswordProtected),
        HasDatabaseConnection = !string.IsNullOrEmpty(x.DatabaseConnectionProtected),
        GitHubUseCredentialManager = x.GitHubUseCredentialManager, GitHubRepositoryId = x.GitHubRepositoryId,
        CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt, LastSuccessfulCommitSha = x.LastSuccessfulCommitSha,
        PollIntervalSeconds = x.PollIntervalSeconds,
        GitHub = new GitHubSettings { RepoOwner = x.RepoOwner, RepoName = x.RepoName, Branch = x.Branch },
        Build = new BuildSettings { SourceCodePath = x.SourceCodePath, ApiProjectPath = x.ApiProjectPath, ReactProjectPath = x.ReactProjectPath, ApiOutputPath = x.ApiOutputPath, ReactOutputPath = x.ReactOutputPath, TimeoutMinutes = x.BuildTimeoutMinutes },
        IIS = new IISSettings { SitePath = x.SitePath, ReactSitePath = x.ReactSitePath, AppPoolName = x.AppPoolName, BackupPath = x.BackupPath, HealthCheckUrl = x.HealthCheckUrl },
        Migration = new MigrationSettings { Enabled = x.MigrationsEnabled, ProjectPath = x.MigrationProjectPath,
            StartupProjectPath = x.MigrationStartupProjectPath, DbContextName = x.MigrationDbContextName,
            ConnectionName = x.MigrationConnectionName, SqlBackupPath = x.SqlBackupPath, TimeoutMinutes = x.MigrationTimeoutMinutes },
        Smtp = new SmtpSettings { Enabled = x.EmailEnabled, Host = x.SmtpHost, Port = x.SmtpPort, Username = x.SmtpUsername, FromAddress = x.SmtpFromAddress, ToAddress = x.SmtpToAddress, EnableSsl = x.SmtpEnableSsl, NotifyOnSuccess = x.NotifyOnSuccess, NotifyOnFailure = x.NotifyOnFailure }
    };

    private static bool SamePath(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private async Task VerifySourceAsync(string ownerUserId, ProjectEditModel model, ProjectEntity? existing)
    {
        if (model.GitHubAuthMode == "Local")
        {
            if (model.GitHubRepositoryId is not long repositoryId)
                throw new InvalidOperationException("Choose a repository from your connected GitHub account.");
            GitHubRepositoryOption? repository;
            try { repository = await githubCredentials.GetRepositoryAsync(repositoryId); }
            catch (Exception ex) { throw new InvalidOperationException("The GitHub repository could not be verified. Reconnect GitHub and try again.", ex); }
            if (repository is null) throw new InvalidOperationException("This repository is not available to your connected GitHub account.");
            model.RepoOwner = repository.Owner;
            model.RepoName = repository.Name;
        }
        else if (string.IsNullOrWhiteSpace(model.GitHubToken) && string.IsNullOrWhiteSpace(existing?.GitHubTokenProtected))
            throw new InvalidOperationException("Enter a GitHub PAT or select a repository through the GitHub connection.");
    }

}
