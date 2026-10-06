using LocalGhost.Shared.Models;

namespace LocalGhost.Dashboard.Models;

public sealed class ProjectEditModel
{
    public ProjectKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Environment { get; set; } = "Production";
    public bool IsActive { get; set; }
    public bool RequiresApproval { get; set; }
    public int PollIntervalSeconds { get; set; } = 30;
    public string RepoOwner { get; set; } = string.Empty;
    public string RepoName { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public string GitHubToken { get; set; } = string.Empty;
    public string GitHubAuthMode { get; set; } = "Local";
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
    public string DatabaseConnectionString { get; set; } = string.Empty;
    public string SqlBackupPath { get; set; } = string.Empty;
    public int MigrationTimeoutMinutes { get; set; } = 10;
    public bool EmailEnabled { get; set; }
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string SmtpFromAddress { get; set; } = string.Empty;
    public string SmtpToAddress { get; set; } = string.Empty;
    public bool SmtpEnableSsl { get; set; } = true;
    public bool NotifyOnSuccess { get; set; } = true;
    public bool NotifyOnFailure { get; set; } = true;
}
