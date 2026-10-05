namespace LocalGhost.Shared.Models;

public enum ProjectKind { AspNet = 0, BlazorWebApp = 1 }

public sealed class ProjectConfiguration
{
    public Guid Id { get; set; }
    public ProjectKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Environment { get; set; } = "Production";
    public bool IsActive { get; set; }
    public bool ForceDeploy { get; set; }
    public string? LastSuccessfulCommitSha { get; set; }
    public int PollIntervalSeconds { get; set; } = 30;
    public GitHubSettings GitHub { get; set; } = new();
    public BuildSettings Build { get; set; } = new();
    public IISSettings IIS { get; set; } = new();
    public SmtpSettings Smtp { get; set; } = new();
}

public sealed class ProjectDeploymentJob
{
    public Guid DeploymentId { get; set; }
    public DateTime StartedAt { get; set; }
    public ProjectConfiguration Project { get; set; } = new();
    public CommitDescriptor Commit { get; set; } = new();
}

public sealed class AgentHeartbeat
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = string.Empty;
    public int ActiveJobs { get; set; }
}

public sealed class CommitDescriptor
{
    public string Sha { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public DateTime CommittedAt { get; set; }
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}

public sealed class ProjectSummary
{
    public Guid Id { get; set; }
    public ProjectKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Environment { get; set; } = "Production";
    public bool IsActive { get; set; }
    public bool HasGitHubToken { get; set; }
    public bool GitHubUseCredentialManager { get; set; }
    public long? GitHubRepositoryId { get; set; }
    public bool HasSmtpPassword { get; set; }
    public bool ManualDeployRequested { get; set; }
    public bool IsDeploymentInProgress { get; set; }
    public DateTime? LastPolledAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? LastSuccessfulCommitSha { get; set; }
    public string? LastSuccessfulBranch { get; set; }
    public DateTime? LastSuccessfulCommittedAt { get; set; }
    public DateTime? LastSuccessfulDeploymentAt { get; set; }
    public DeployStatus? LastDeploymentStatus { get; set; }
    public DateTime? LastDeploymentAt { get; set; }
    public int DeploymentCount { get; set; }
    public int SuccessRate { get; set; }
    public int PollIntervalSeconds { get; set; } = 30;
    public GitHubSettings GitHub { get; set; } = new();
    public BuildSettings Build { get; set; } = new();
    public IISSettings IIS { get; set; } = new();
    public SmtpSettings Smtp { get; set; } = new();
}
