using System.ComponentModel.DataAnnotations;
using LocalGhost.Shared.Models;

namespace LocalGhost.Dashboard.Data;

public sealed class ProjectEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectGroupId { get; set; }
    public ProjectKind Kind { get; set; }
    [MaxLength(450)] public string OwnerUserId { get; set; } = string.Empty;
    [MaxLength(100)] public string Name { get; set; } = string.Empty;
    [MaxLength(500)] public string Description { get; set; } = string.Empty;
    [MaxLength(50)] public string Environment { get; set; } = "Production";
    public bool IsActive { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsArchived { get; set; }
    public bool ManualDeployRequested { get; set; }
    public bool IsDeploymentInProgress { get; set; }
    public DateTime? AgentLeaseUntil { get; set; }
    public DateTime? LastPolledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? LastSuccessfulCommitSha { get; set; }

    public int PollIntervalSeconds { get; set; } = 30;
    public string RepoOwner { get; set; } = string.Empty;
    public string RepoName { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public string GitHubTokenProtected { get; set; } = string.Empty;
    public bool GitHubUseCredentialManager { get; set; }
    public long? GitHubRepositoryId { get; set; }

    public string SourceCodePath { get; set; } = string.Empty;
    public string ApiProjectPath { get; set; } = string.Empty;
    public string ReactProjectPath { get; set; } = string.Empty;
    public string ApiOutputPath { get; set; } = string.Empty;
    public string ReactOutputPath { get; set; } = string.Empty;
    public int BuildTimeoutMinutes { get; set; } = 10;

    public string SitePath { get; set; } = string.Empty;
    public string ReactSitePath { get; set; } = string.Empty;
    public string AppPoolName { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public string HealthCheckUrl { get; set; } = string.Empty;
    public bool MigrationsEnabled { get; set; }
    public string MigrationProjectPath { get; set; } = string.Empty;
    public string MigrationStartupProjectPath { get; set; } = string.Empty;
    public string MigrationDbContextName { get; set; } = string.Empty;
    public string MigrationConnectionName { get; set; } = "DefaultConnection";
    public string DatabaseConnectionProtected { get; set; } = string.Empty;
    public string SqlBackupPath { get; set; } = string.Empty;
    public int MigrationTimeoutMinutes { get; set; } = 10;

    public bool EmailEnabled { get; set; }
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPasswordProtected { get; set; } = string.Empty;
    public string SmtpFromAddress { get; set; } = string.Empty;
    public string SmtpToAddress { get; set; } = string.Empty;
    public bool SmtpEnableSsl { get; set; } = true;
    public bool NotifyOnSuccess { get; set; } = true;
    public bool NotifyOnFailure { get; set; } = true;
}

public enum ProjectMemberRole { Viewer = 0, Deployer = 1, Manager = 2 }

public sealed class ProjectMemberEntity
{
    public Guid ProjectGroupId { get; set; }
    [MaxLength(450)] public string UserId { get; set; } = string.Empty;
    public ProjectMemberRole Role { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ProjectEnvironmentGrantEntity
{
    public Guid ProjectId { get; set; }
    [MaxLength(450)] public string UserId { get; set; } = string.Empty;
    public ProjectMemberRole Role { get; set; }
}

public sealed class ProjectInvitationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectGroupId { get; set; }
    [MaxLength(64)] public string TokenHash { get; set; } = string.Empty;
    public ProjectMemberRole Role { get; set; }
    public Guid? EnvironmentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RedeemedAt { get; set; }
    [MaxLength(450)] public string? RedeemedByUserId { get; set; }
}

public sealed class DeploymentApprovalEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    [MaxLength(200)] public string Branch { get; set; } = string.Empty;
    [MaxLength(100)] public string CommitSha { get; set; } = string.Empty;
    [MaxLength(450)] public string RequestedByUserId { get; set; } = string.Empty;
    [MaxLength(450)] public string? DecidedByUserId { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public bool? Approved { get; set; }
}

public sealed class DeploymentRunEntity
{
    public Guid Id { get; set; }
    public bool IsHidden { get; set; }
    public Guid ProjectId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public string CommitMessage { get; set; } = string.Empty;
    public string CommitAuthor { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public DateTime? CommittedAt { get; set; }
    public int Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string FullLog { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string? FailedStep { get; set; }
}

public sealed class ProjectBranchBaselineEntity
{
    public Guid ProjectId { get; set; }
    public string Branch { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
}

public sealed class DeploymentLogEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid DeployId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Level { get; set; }
    public string Source { get; set; } = string.Empty;
}
