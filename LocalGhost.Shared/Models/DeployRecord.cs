namespace LocalGhost.Shared.Models;

public class DeployRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // The GitHub commit that triggered this deploy (testing)
    public string CommitSha { get; set; } = string.Empty;

    // Short 7-char SHA shown in UI  e.g. "a3f91bc"
    public string ShortSha => CommitSha.Length >= 7
        ? CommitSha[..7]
        : CommitSha;

    public string CommitMessage { get; set; } = string.Empty;
    public string CommitAuthor { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    // How long the full deploy took
    public TimeSpan? Duration => FinishedAt.HasValue
        ? FinishedAt.Value - StartedAt
        : null;

    public string DurationDisplay => Duration.HasValue
        ? $"{(int)Duration.Value.TotalSeconds}s"
        : "running...";

    public DeployStatus Status { get; set; } = DeployStatus.Running;

    // Full log captured from build + deploy steps
    public string FullLog { get; set; } = string.Empty;

    // Human-readable error if status is Failed
    public string? ErrorMessage { get; set; }

    // Which step failed: "Build" | "Deploy" | "Notify"
    public string? FailedStep { get; set; }
}

public enum DeployStatus
{
    Running,
    Success,
    Failed
}