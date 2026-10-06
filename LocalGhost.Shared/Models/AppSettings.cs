namespace LocalGhost.Shared.Models;

public class GitHubSettings
{
    public string RepoOwner { get; set; } = string.Empty;
    public string RepoName { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
}

public class BuildSettings
{
    // Server folder where git clones/pulls the repo source code
    public string SourceCodePath { get; set; } = string.Empty;

    // .csproj path — now points inside SourceCodePath on the server
    public string ApiProjectPath { get; set; } = string.Empty;

    // React app folder — now points inside SourceCodePath on the server
    public string ReactProjectPath { get; set; } = string.Empty;

    // Final compiled output → goes straight to IIS
    public string ApiOutputPath { get; set; } = string.Empty;

    // Final React build output → goes straight to IIS
    public string ReactOutputPath { get; set; } = string.Empty;
    public int TimeoutMinutes { get; set; } = 10;
}

public class IISSettings
{
    public string SitePath { get; set; } = string.Empty;
    public string ReactSitePath { get; set; } = string.Empty;
    public string AppPoolName { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public string HealthCheckUrl { get; set; } = string.Empty;
}

public class MigrationSettings
{
    public bool Enabled { get; set; }
    public string ProjectPath { get; set; } = string.Empty;
    public string StartupProjectPath { get; set; } = string.Empty;
    public string DbContextName { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = "DefaultConnection";
    public string ConnectionString { get; set; } = string.Empty;
    public string SqlBackupPath { get; set; } = string.Empty;
    public int TimeoutMinutes { get; set; } = 10;
}

public class SmtpSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;
    public bool NotifyOnSuccess { get; set; } = true;
    public bool NotifyOnFailure { get; set; } = true;
}
