namespace LocalGhost.Dashboard.Models;

public enum PreflightCheckStatus
{
    Passed,
    Warning,
    Failed
}

public sealed record PreflightCheck(string Name, string Detail, PreflightCheckStatus Status);

public sealed record ProjectPreflightReport(
    Guid ProjectId,
    DateTime CheckedAt,
    IReadOnlyList<PreflightCheck> Checks)
{
    public bool CanDeploy => Checks.All(x => x.Status != PreflightCheckStatus.Failed);
    public int PassedCount => Checks.Count(x => x.Status == PreflightCheckStatus.Passed);
    public int WarningCount => Checks.Count(x => x.Status == PreflightCheckStatus.Warning);
    public int FailedCount => Checks.Count(x => x.Status == PreflightCheckStatus.Failed);
}
