namespace LocalGhost.Shared.Models;

public class LogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }

    // Which deploy this log line belongs to
    public Guid DeployId { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string Message { get; set; } = string.Empty;

    public LogLevel Level { get; set; } = LogLevel.Info;

    // Which stage produced this line: "Dashboard" | "BuildRunner" | "IISDeployer" | "Agent"
    public string Source { get; set; } = string.Empty;

    // Short timestamp shown in the Blazor log feed  e.g. "14:32:05"
    public string TimeDisplay => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    // CSS class name used in Blazor to colour the row
    public string CssClass => Level switch
    {
        LogLevel.Error => "log-error",
        LogLevel.Warning => "log-warning",
        LogLevel.Success => "log-success",
        _ => "log-info"
    };
}

public enum LogLevel
{
    Info,
    Warning,
    Error,
    Success
}
