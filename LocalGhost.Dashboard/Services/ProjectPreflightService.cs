using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using LocalGhost.Dashboard.Data;
using LocalGhost.Dashboard.Models;
using LocalGhost.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Octokit;

namespace LocalGhost.Dashboard.Services;

public sealed class ProjectPreflightService(
    IDbContextFactory<ProjectDbContext> dbFactory,
    GitHubCredentialService githubCredentials,
    DeployStateService agentState,
    IConfiguration configuration,
    ILogger<ProjectPreflightService> logger)
{
    private readonly ConcurrentDictionary<(string OwnerId, Guid ProjectId), ProjectPreflightReport> _latest = new();

    public ProjectPreflightReport? GetLatest(string ownerId, Guid projectId) =>
        _latest.TryGetValue((ownerId, projectId), out var report) ? report : null;

    public void Dismiss(string ownerId, Guid projectId) => _latest.TryRemove((ownerId, projectId), out _);

    public async Task<ProjectPreflightReport?> RunAsync(string ownerId, Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == projectId && !x.IsArchived &&
                (x.OwnerUserId == ownerId || db.ProjectMembers.Any(m => m.ProjectGroupId == x.ProjectGroupId && m.UserId == ownerId)), cancellationToken);
        if (project is null) return null;

        var checks = new List<PreflightCheck>();
        await CheckGitHubAsync(project, checks, cancellationToken);
        await CheckCommandAsync("Git", "git", "--version", checks, cancellationToken);
        await CheckCommandAsync(".NET SDK", "dotnet", "--version", checks, cancellationToken);
        if (!string.IsNullOrWhiteSpace(project.ReactProjectPath))
            await CheckCommandAsync("Node and npm", OperatingSystem.IsWindows() ? "cmd.exe" : "npm",
                OperatingSystem.IsWindows() ? "/d /s /c \"npm.cmd --version\"" : "--version", checks, cancellationToken);

        var sourceIsPresent = HasSource(project.SourceCodePath);
        CheckFile(project.Kind == ProjectKind.BlazorWebApp ? "Blazor Web App project" : ".NET project", project.ApiProjectPath, checks, sourceIsPresent);
        if (!string.IsNullOrWhiteSpace(project.ReactProjectPath)) CheckDirectory("React project", project.ReactProjectPath, checks, sourceIsPresent);
        CheckWritableParent("Source checkout", project.SourceCodePath, checks);
        CheckWritableParent(".NET publish output", project.ApiOutputPath, checks);
        if (!string.IsNullOrWhiteSpace(project.ReactProjectPath)) CheckWritableParent("React build output", project.ReactOutputPath, checks);
        // The Dashboard prepares source/build paths, but the Agent writes IIS and backup paths.
        // Probing those paths here tests the wrong Windows identity (often a non-admin Dashboard).
        CheckAgentDirectory(project.Kind == ProjectKind.BlazorWebApp ? "IIS Blazor site" : "IIS API site", project.SitePath, checks);
        if (!string.IsNullOrWhiteSpace(project.ReactProjectPath)) CheckAgentDirectory("IIS frontend site", project.ReactSitePath, checks);
        CheckAgentDirectory("Rollback backup", project.BackupPath, checks);
        if (project.MigrationsEnabled)
        {
            checks.Add(string.IsNullOrWhiteSpace(configuration["AgentApiKey"])
                ? Fail("Agent channel security", "Migrations need a shared AgentApiKey. Set the same non-empty AgentApiKey via configuration or environment variables on both Dashboard and Agent, then restart both services. Do not commit the key to Git.")
                : Pass("Agent channel security", "Agent API key is configured on the Dashboard."));
            checks.Add(string.IsNullOrWhiteSpace(project.DatabaseConnectionProtected)
                ? Fail("Database connection", "No SQL Server deployment connection is stored for this environment.")
                : Pass("Database connection", "SQL Server deployment connection is stored encrypted."));
            checks.Add(string.IsNullOrWhiteSpace(project.SqlBackupPath) || !Path.IsPathFullyQualified(project.SqlBackupPath)
                ? Fail("SQL Server backup", "Set an absolute backup path on the SQL Server machine.")
                : Pass("SQL Server backup", "Backup location configured; SQL Server write access is tested during deployment."));
            CheckFile("EF migrations project", project.MigrationProjectPath, checks, sourceIsPresent);
            checks.Add(AgentToolCheck("EF Core tools", agentState.AgentOnline ? agentState.AgentEfToolsStatus : null,
                "Install dotnet-ef for the Agent account (or restore it where the Agent runs), then restart the Agent."));
            checks.Add(AgentToolCheck("SQL Server sqlcmd", agentState.AgentOnline ? agentState.AgentSqlcmdStatus : null,
                "Install sqlcmd for the Agent account and restart the Agent."));
        }
        checks.Add(Warn("IIS application pool", $"The Agent must be able to inspect and restart application pool '{project.AppPoolName}'. Dashboard permissions do not prove Agent permissions."));
        await CheckHealthAsync(project.HealthCheckUrl, checks, cancellationToken);

        var report = new ProjectPreflightReport(project.Id, DateTime.UtcNow, checks);
        _latest[(ownerId, projectId)] = report;
        return report;
    }

    private async Task CheckGitHubAsync(ProjectEntity project, List<PreflightCheck> checks, CancellationToken cancellationToken)
    {
        try
        {
            var token = await githubCredentials.GetProjectTokenAsync(project, cancellationToken);
            var github = new GitHubClient(new ProductHeaderValue("LocalGhost-Preflight")) { Credentials = new Credentials(token) };
            var branches = ProjectSourceService.ParseBranches(project.Branch);
            foreach (var branch in branches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var commits = await github.Repository.Commit.GetAll(project.RepoOwner, project.RepoName, new CommitRequest { Sha = branch });
                if (commits.Count == 0) throw new InvalidOperationException($"Branch '{branch}' contains no commits.");
            }
            checks.Add(Pass("GitHub connection", $"Repository is accessible and {branches.Count} configured branch{(branches.Count == 1 ? "" : "es")} were found."));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GitHub preflight failed for project {ProjectId}", project.Id);
            checks.Add(Fail("GitHub connection", FriendlyGitHubError(ex)));
        }
    }

    private static async Task CheckCommandAsync(string name, string fileName, string arguments,
        List<PreflightCheck> checks, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(fileName, arguments, cancellationToken);
        checks.Add(result.Success ? Pass(name, $"Available ({result.Output}).") : Fail(name, result.Output));
    }

    private static async Task CheckHealthAsync(string url, List<PreflightCheck> checks, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            checks.Add(Warn("Health endpoint", "No health-check URL is configured; deployment will only verify the IIS application pool."));
            return;
        }

        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                    errors == SslPolicyErrors.None || request.RequestUri?.IsLoopback == true
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await client.GetAsync(url, cancellationToken);
            checks.Add(response.IsSuccessStatusCode
                ? Pass("Health endpoint", $"Responded with HTTP {(int)response.StatusCode}.")
                : Warn("Health endpoint", $"Currently responds with HTTP {(int)response.StatusCode}. Deployment may still repair it."));
        }
        catch (Exception ex)
        {
            checks.Add(Warn("Health endpoint", $"Not reachable before deployment: {ex.Message}"));
        }
    }

    private static void CheckFile(string name, string path, List<PreflightCheck> checks, bool sourceIsPresent) =>
        checks.Add(File.Exists(path)
            ? Pass(name, path)
            : sourceIsPresent ? Fail(name, $"File not found: {path}") : Warn(name, "Will be verified after the first source checkout."));

    private static void CheckDirectory(string name, string path, List<PreflightCheck> checks, bool sourceIsPresent) =>
        checks.Add(Directory.Exists(path)
            ? Pass(name, path)
            : sourceIsPresent ? Fail(name, $"Directory not found: {path}") : Warn(name, "Will be verified after the first source checkout."));

    private static void CheckWritableParent(string name, string path, List<PreflightCheck> checks)
    {
        var existing = FindExistingDirectory(path);
        if (existing is null) checks.Add(Fail(name, $"No accessible parent directory exists for {path}"));
        else CheckWrite(name, existing, checks, $"Parent directory is writable and can create {path}.");
    }

    private static void CheckAgentDirectory(string name, string path, List<PreflightCheck> checks)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            checks.Add(Fail(name, "Configure an absolute path on the Agent machine."));
        else
            checks.Add(Warn(name, $"Agent must have write access to {path}. Dashboard cannot verify the Agent service account's permissions."));
    }

    private static PreflightCheck AgentToolCheck(string name, string? status, string resolution)
    {
        if (status is null) return Fail(name, "No recent tool report from the Agent. Start or update the Agent and wait for its next heartbeat.");
        return status.StartsWith("Available:", StringComparison.OrdinalIgnoreCase)
            ? Pass(name, $"Agent reports {status}")
            : Fail(name, $"Agent reports {status} {resolution}");
    }

    private static void CheckWrite(string name, string directory, List<PreflightCheck> checks, string successMessage)
    {
        var probe = Path.Combine(directory, $".localghost-preflight-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "LocalGhost preflight");
            File.Delete(probe);
            checks.Add(Pass(name, successMessage));
        }
        catch (Exception ex)
        {
            checks.Add(Fail(name, $"Write permission failed for {directory}: {ex.Message}"));
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
    }

    private static string? FindExistingDirectory(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var current = Directory.Exists(path) ? new DirectoryInfo(path) : Directory.GetParent(path);
            while (current is not null && !current.Exists) current = current.Parent;
            return current?.FullName;
        }
        catch { return null; }
    }

    private static bool HasSource(string path)
    {
        try { return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(); }
        catch { return false; }
    }

    private static async Task<(bool Success, string Output)> RunCommandAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName, Arguments = arguments, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = FirstLine(await stdout);
            var error = (await stderr).Trim();
            return process.ExitCode == 0
                ? (true, string.IsNullOrWhiteSpace(output) ? "ready" : output)
                : (false, string.IsNullOrWhiteSpace(error)
                    ? $"{fileName} exited with code {process.ExitCode}."
                    : error.Length > 2000 ? error[..2000] + "…" : error);
        }
        catch (Exception ex)
        {
            return (false, $"Unavailable: {ex.Message}");
        }
    }

    private static string FirstLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    private static string FriendlyGitHubError(Exception ex) => ex switch
    {
        NotFoundException => "Repository or one of the configured branches was not found. Check repository access and branch names.",
        AuthorizationException => "The saved GitHub token was rejected. Grant this repository Contents: Read access.",
        _ => ex.Message
    };
    private static PreflightCheck Pass(string name, string detail) => new(name, detail, PreflightCheckStatus.Passed);
    private static PreflightCheck Warn(string name, string detail) => new(name, detail, PreflightCheckStatus.Warning);
    private static PreflightCheck Fail(string name, string detail) => new(name, detail, PreflightCheckStatus.Failed);
}
