using LocalGhost.Dashboard.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;
using System.Text;

namespace LocalGhost.Dashboard.Services;

public sealed class ProjectWorkspaceService(
    IDbContextFactory<ProjectDbContext> dbFactory,
    ProjectAccessService access,
    DeployStateService state)
{
    public async Task<string> CreateInvitationAsync(string actorId, Guid projectId, ProjectMemberRole role, Guid? environmentId)
    {
        if (!Enum.IsDefined(role)) throw new InvalidOperationException("Invalid role.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.SingleAsync(x => x.Id == projectId && !x.IsArchived);
        if (project.OwnerUserId != actorId) throw new UnauthorizedAccessException("Only the project owner can invite teammates.");
        if (environmentId is { } target && !await db.Projects.AnyAsync(x => x.Id == target &&
            x.ProjectGroupId == project.ProjectGroupId && !x.IsArchived && x.IsActive &&
            x.SitePath != "" && x.AppPoolName != "" && x.BackupPath != "" && x.LastSuccessfulCommitSha != null))
            throw new InvalidOperationException("Deploy the environment successfully before creating a scoped invitation.");
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        db.ProjectInvitations.Add(new ProjectInvitationEntity
        {
            ProjectGroupId = project.ProjectGroupId, TokenHash = Hash(token), Role = role,
            EnvironmentId = environmentId, ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync();
        return token;
    }

    public async Task<InvitationView?> GetInvitationAsync(string token)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var invite = await db.ProjectInvitations.AsNoTracking().FirstOrDefaultAsync(x =>
            x.TokenHash == Hash(token) && x.RedeemedAt == null && x.ExpiresAt > now);
        if (invite is null) return null;
        if (invite.EnvironmentId is { } invitedEnvironment && !await db.Projects.AnyAsync(x =>
            x.Id == invitedEnvironment && !x.IsArchived && x.IsActive && x.LastSuccessfulCommitSha != null)) return null;
        var project = await db.Projects.AsNoTracking().Where(x => x.ProjectGroupId == invite.ProjectGroupId && !x.IsArchived)
            .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
        if (project is null) return null;
        var environment = invite.EnvironmentId is null ? "All environments" :
            await db.Projects.AsNoTracking().Where(x => x.Id == invite.EnvironmentId).Select(x => x.Environment).FirstOrDefaultAsync() ?? "Environment";
        return new InvitationView(project.Name, project.Id, invite.Role, environment, invite.ExpiresAt);
    }

    public async Task<Guid?> RedeemInvitationAsync(string userId, string token)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var now = DateTime.UtcNow;
        var invite = await db.ProjectInvitations.AsNoTracking().FirstOrDefaultAsync(x =>
            x.TokenHash == Hash(token) && x.RedeemedAt == null && x.ExpiresAt > now);
        if (invite is null) return null;
        if (invite.EnvironmentId is { } invitedEnvironment && !await db.Projects.AnyAsync(x =>
            x.Id == invitedEnvironment && !x.IsArchived && x.IsActive && x.LastSuccessfulCommitSha != null)) return null;
        var project = await db.Projects.AsNoTracking().Where(x => x.ProjectGroupId == invite.ProjectGroupId && !x.IsArchived)
            .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
        if (project is null) return null;
        if (project.OwnerUserId == userId) return project.Id;
        var claimed = await db.ProjectInvitations.Where(x => x.Id == invite.Id && x.RedeemedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.RedeemedAt, now)
                .SetProperty(x => x.RedeemedByUserId, userId));
        if (claimed != 1) return null;
        var member = await db.ProjectMembers.FindAsync(invite.ProjectGroupId, userId);
        if (member is null)
            db.ProjectMembers.Add(new ProjectMemberEntity { ProjectGroupId = invite.ProjectGroupId, UserId = userId,
                Role = invite.EnvironmentId is null ? invite.Role : ProjectMemberRole.Viewer });
        else member.Role = invite.EnvironmentId is null ? invite.Role : ProjectMemberRole.Viewer;
        var ids = await db.Projects.Where(x => x.ProjectGroupId == invite.ProjectGroupId).Select(x => x.Id).ToListAsync();
        var existing = await db.ProjectEnvironmentGrants.Where(x => x.UserId == userId && ids.Contains(x.ProjectId)).ToListAsync();
        db.ProjectEnvironmentGrants.RemoveRange(existing);
        if (invite.EnvironmentId is { } environmentId)
            db.ProjectEnvironmentGrants.Add(new ProjectEnvironmentGrantEntity { ProjectId = environmentId, UserId = userId, Role = invite.Role });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return project.Id;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public async Task<List<ProjectEntity>> GetEnvironmentsAsync(string userId, Guid projectId)
    {
        if (!await access.CanAsync(userId, projectId, ProjectMemberRole.Viewer)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var group = await db.Projects.Where(x => x.Id == projectId).Select(x => x.ProjectGroupId).SingleAsync();
        return await db.Projects.AsNoTracking().Where(x => x.ProjectGroupId == group && !x.IsArchived)
            .OrderBy(x => x.CreatedAt).ToListAsync();
    }

    public async Task<List<ProjectEntity>> GetArchivedEnvironmentsAsync(string userId, Guid projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId && !x.IsArchived);
        if (project is null || project.OwnerUserId != userId) return [];
        return await db.Projects.AsNoTracking().Where(x => x.ProjectGroupId == project.ProjectGroupId && x.IsArchived)
            .OrderByDescending(x => x.UpdatedAt).ToListAsync();
    }

    public async Task<Guid> ArchiveEnvironmentAsync(string userId, Guid projectId, Guid environmentId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.SingleAsync(x => x.Id == projectId && !x.IsArchived);
        if (project.OwnerUserId != userId) throw new UnauthorizedAccessException("Only the project owner can remove an environment.");
        var target = await db.Projects.SingleOrDefaultAsync(x => x.Id == environmentId &&
            x.ProjectGroupId == project.ProjectGroupId && !x.IsArchived);
        if (target is null) throw new InvalidOperationException("Environment not found.");
        if (target.IsActive || target.IsDeploymentInProgress || state.GetCurrent(environmentId) is not null)
            throw new InvalidOperationException("Pause monitoring and wait for any running deployment before removing this environment.");
        var replacement = await db.Projects.AsNoTracking().Where(x => x.ProjectGroupId == project.ProjectGroupId &&
            x.Id != environmentId && !x.IsArchived).OrderBy(x => x.CreatedAt).Select(x => x.Id).FirstOrDefaultAsync();
        if (replacement == Guid.Empty)
            throw new InvalidOperationException("A project must keep at least one environment. Add another target before removing this one.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        target.IsArchived = true;
        target.IsActive = false;
        target.ManualDeployRequested = false;
        target.UpdatedAt = DateTime.UtcNow;
        await db.DeploymentApprovals.Where(x => x.ProjectId == environmentId && x.ConsumedAt == null &&
            (x.Approved == null || x.Approved == true)).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Approved, false)
                .SetProperty(x => x.DecidedByUserId, userId)
                .SetProperty(x => x.DecidedAt, target.UpdatedAt)
                .SetProperty(x => x.ConsumedAt, target.UpdatedAt));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return replacement;
    }

    public async Task RestoreEnvironmentAsync(string userId, Guid projectId, Guid environmentId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.SingleAsync(x => x.Id == projectId && !x.IsArchived);
        if (project.OwnerUserId != userId) throw new UnauthorizedAccessException("Only the project owner can restore an environment.");
        var target = await db.Projects.SingleOrDefaultAsync(x => x.Id == environmentId &&
            x.ProjectGroupId == project.ProjectGroupId && x.IsArchived);
        if (target is null) throw new InvalidOperationException("Removed environment not found.");
        if (await db.Projects.AnyAsync(x => x.ProjectGroupId == project.ProjectGroupId && !x.IsArchived &&
            x.Environment.ToLower() == target.Environment.ToLower()))
            throw new InvalidOperationException("An active environment already uses that name.");
        target.IsArchived = false;
        target.IsActive = false;
        target.ManualDeployRequested = false;
        target.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<List<ProjectMemberEntity>> GetMembersAsync(string userId, Guid projectId)
    {
        if (!await access.CanAsync(userId, projectId, ProjectMemberRole.Viewer)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var group = await db.Projects.Where(x => x.Id == projectId).Select(x => x.ProjectGroupId).SingleAsync();
        return await db.ProjectMembers.AsNoTracking().Where(x => x.ProjectGroupId == group)
            .OrderBy(x => x.AddedAt).ToListAsync();
    }

    public async Task<List<ProjectEnvironmentGrantEntity>> GetGrantsAsync(string userId, Guid projectId)
    {
        if (!await access.CanAsync(userId, projectId, ProjectMemberRole.Viewer)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var group = await db.Projects.Where(x => x.Id == projectId).Select(x => x.ProjectGroupId).SingleAsync();
        var ids = await db.Projects.Where(x => x.ProjectGroupId == group && !x.IsArchived).Select(x => x.Id).ToListAsync();
        return await db.ProjectEnvironmentGrants.AsNoTracking().Where(x => ids.Contains(x.ProjectId)).ToListAsync();
    }

    public async Task<Guid> AddEnvironmentAsync(string userId, Guid projectId, string name)
    {
        name = name.Trim();
        if (name.Length is < 2 or > 50) throw new InvalidOperationException("Environment name must be 2–50 characters.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var source = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectId && !x.IsArchived);
        if (source.OwnerUserId != userId)
            throw new UnauthorizedAccessException("Only the project owner can add environments.");
        if (await db.Projects.AnyAsync(x => x.ProjectGroupId == source.ProjectGroupId && !x.IsArchived && x.Environment.ToLower() == name.ToLower()))
            throw new InvalidOperationException("That environment already exists.");
        var suffix = string.Concat(name.Where(char.IsLetterOrDigit));
        var target = new ProjectEntity
        {
            ProjectGroupId = source.ProjectGroupId, OwnerUserId = source.OwnerUserId,
            Name = source.Name, Description = source.Description, Environment = name,
            Kind = source.Kind, IsActive = false, RequiresApproval = true,
            PollIntervalSeconds = source.PollIntervalSeconds,
            RepoOwner = source.RepoOwner, RepoName = source.RepoName, Branch = source.Branch,
            GitHubTokenProtected = source.GitHubTokenProtected,
            GitHubUseCredentialManager = source.GitHubUseCredentialManager,
            GitHubRepositoryId = source.GitHubRepositoryId,
            SourceCodePath = source.SourceCodePath + "-" + suffix,
            ApiProjectPath = InNewCheckout(source.ApiProjectPath, source.SourceCodePath, suffix),
            ReactProjectPath = InNewCheckout(source.ReactProjectPath, source.SourceCodePath, suffix),
            ApiOutputPath = source.ApiOutputPath + "-" + suffix,
            ReactOutputPath = string.IsNullOrWhiteSpace(source.ReactOutputPath) ? "" : source.ReactOutputPath + "-" + suffix,
            BuildTimeoutMinutes = source.BuildTimeoutMinutes,
            // Targets are intentionally empty: never clone a live IIS destination.
            SitePath = "", ReactSitePath = "", AppPoolName = "", BackupPath = "", HealthCheckUrl = "",
            MigrationsEnabled = false, MigrationProjectPath = InNewCheckout(source.MigrationProjectPath, source.SourceCodePath, suffix),
            MigrationStartupProjectPath = InNewCheckout(source.MigrationStartupProjectPath, source.SourceCodePath, suffix),
            MigrationDbContextName = source.MigrationDbContextName, MigrationConnectionName = source.MigrationConnectionName,
            DatabaseConnectionProtected = "", SqlBackupPath = "",
            EmailEnabled = false
        };
        db.Projects.Add(target);
        await db.SaveChangesAsync();
        return target.Id;
    }

    private static string InNewCheckout(string path, string sourceRoot, string suffix) =>
        !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(sourceRoot) &&
        path.StartsWith(sourceRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? sourceRoot + "-" + suffix + path[sourceRoot.Length..]
            : path;

    public async Task RemoveMemberAsync(string actorId, Guid projectId, string memberId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.SingleAsync(x => x.Id == projectId && !x.IsArchived);
        if (project.OwnerUserId != actorId)
            throw new UnauthorizedAccessException("Only the project owner can manage members.");
        var member = await db.ProjectMembers.FindAsync(project.ProjectGroupId, memberId);
        if (member is null) return;
        var environmentIds = await db.Projects.Where(x => x.ProjectGroupId == project.ProjectGroupId).Select(x => x.Id).ToListAsync();
        var grants = await db.ProjectEnvironmentGrants.Where(x => x.UserId == memberId && environmentIds.Contains(x.ProjectId)).ToListAsync();
        db.ProjectEnvironmentGrants.RemoveRange(grants);
        db.ProjectMembers.Remove(member);
        await db.SaveChangesAsync();
    }
}

public sealed record InvitationView(string ProjectName, Guid ProjectId, ProjectMemberRole Role,
    string Environment, DateTime ExpiresAt);
