namespace LocalGhost.Shared.Models;

public class AppSettings
{
    public GitHubSettings GitHub { get; set; } = new();
    public BuildSettings Build { get; set; } = new();
    public IISSettings IIS { get; set; } = new();
    public SmtpSettings Smtp { get; set; } = new();
    public int PollIntervalSeconds { get; set; } = 30;
}

public class GitHubSettings
{
    public string Token { get; set; } = string.Empty;
    public string RepoOwner { get; set; } = string.Empty;
    public string RepoName { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
}

public class BuildSettings
{
    // Full path to your .NET API .csproj file
    public string ApiProjectPath { get; set; } = string.Empty;

    // Full path to your React app folder (where package.json lives)
    public string ReactProjectPath { get; set; } = string.Empty;

    // Where dotnet publish dumps the output
    public string ApiOutputPath { get; set; } = string.Empty;

    // Where npm run build dumps the output (usually /build or /dist)
    public string ReactOutputPath { get; set; } = string.Empty;
}

public class IISSettings
{
    // Full path to IIS site folder on the server
    public string SitePath { get; set; } = string.Empty;

    // Name of the IIS Application Pool (must match exactly)
    public string AppPoolName { get; set; } = string.Empty;

    // LocalGhost keeps one backup folder here before overwriting
    public string BackupPath { get; set; } = string.Empty;
}

public class SmtpSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;

    // If false, only send email on failure (not every success)
    public bool NotifyOnSuccess { get; set; } = true;
}