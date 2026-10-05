using LocalGhost.Shared.Models;
using Microsoft.Web.Administration;
using System.Diagnostics;
using System.Text;

namespace LocalGhost.Services;

public class IISDeployer
{
    private readonly ILogger<IISDeployer> _logger;

    public IISDeployer(ILogger<IISDeployer> logger) => _logger = logger;

    /// <summary>
    /// Full deploy pipeline:
    /// 1. Backup current IIS files
    /// 2. Stop app pool
    /// 3. Copy API output → IIS site folder
    /// 4. Copy React output → IIS front folder
    /// 5. Start app pool
    /// 6. Verify pool is running
    /// On any failure → auto rollback from backup
    /// </summary>
    public async Task<DeployResult> DeployAsync(BuildResult buildResult, ProjectConfiguration project,
        Func<PipelineStage, string, Task>? onProgress = null)
    {
        var iis = project.IIS;
        var build = project.Build;
        var appLabel = project.Kind == ProjectKind.BlazorWebApp ? "Blazor Web App" : "API";
        var log = new StringBuilder();
        var startedAt = DateTime.UtcNow;
        var commit = buildResult.Commit;
        Task Progress(PipelineStage stage, string message) => onProgress?.Invoke(stage, message) ?? Task.CompletedTask;

        _logger.LogInformation("═══════════════════════════════════════");
        _logger.LogInformation("DEPLOY STARTED for commit {Sha}", commit.ShortSha);
        _logger.LogInformation("═══════════════════════════════════════");
        log.AppendLine($"Deploy started at {startedAt:yyyy-MM-dd HH:mm:ss} UTC");
        log.AppendLine($"Commit: {commit.ShortSha} — {commit.Message}");
        log.AppendLine();

        // ── Step 1: Backup ─────────────────────────────────────
        var backupPath = Path.Combine(iis.BackupPath, $"backup_{commit.ShortSha}_{DateTime.Now:yyyyMMdd_HHmmss}");

        await Progress(PipelineStage.Deploy, "Creating rollback backup");
        _logger.LogInformation("▶ Creating backup at {Path}", backupPath);
        log.AppendLine($"──── backup ────");

        var backupOk = CreateBackup(backupPath, log, iis);
        if (!backupOk)
        {
            _logger.LogWarning("Backup failed — continuing anyway (no previous version to restore)");
            log.AppendLine("WARNING: Backup skipped — no existing files to back up");
        }
        else
        {
            _logger.LogInformation("✔ Backup created → {Path}", backupPath);
            log.AppendLine($"RESULT: Backup OK → {backupPath}");
        }
        log.AppendLine();

        // ── Step 2: Stop app pool ──────────────────────────────
        await Progress(PipelineStage.Deploy, $"Stopping IIS app pool {iis.AppPoolName}");
        _logger.LogInformation("▶ Stopping IIS app pool: {Pool}", iis.AppPoolName);
        log.AppendLine("──── IIS pool stop ────");

        var stopOk = await SetAppPoolStateAsync(log, iis, "stop");
        if (!stopOk)
        {
            return DeployResult.Fail(commit,
                buildResult.FullLog + log.ToString(),
                "Failed to stop IIS app pool");
        }

        _logger.LogInformation("✔ App pool stopped");
        log.AppendLine("RESULT: App pool stopped");
        log.AppendLine();

        // ── Step 3: Copy API files ─────────────────────────────
        await Progress(PipelineStage.Deploy, $"Publishing {appLabel} files to IIS");
        _logger.LogInformation("▶ Copying {App} files → {Dest}", appLabel, iis.SitePath);
        log.AppendLine($"──── copy {appLabel} files ────");

        var apiCopyOk = CopyDirectory(
            source: build.ApiOutputPath,
            dest: iis.SitePath,
            log);

        if (!apiCopyOk)
        {
            _logger.LogError("✖ API copy FAILED — rolling back");
            await RollbackAsync(backupPath, log, iis);
            await SetAppPoolStateAsync(log, iis, "start");
            return DeployResult.Fail(commit,
                buildResult.FullLog + log.ToString(),
                $"Failed to copy {appLabel} files to IIS folder");
        }

        _logger.LogInformation("✔ {App} files copied → {Path}", appLabel, iis.SitePath);
        log.AppendLine($"RESULT: {appLabel} copy OK → {iis.SitePath}");
        log.AppendLine();

        // ── Step 4: Copy React files ───────────────────────────
        if (!string.IsNullOrWhiteSpace(iis.ReactSitePath))
        {
            await Progress(PipelineStage.Deploy, "Publishing frontend files to IIS");
            _logger.LogInformation("▶ Copying React files → {Dest}", iis.ReactSitePath);
            log.AppendLine("──── copy React files ────");

            // Clear old React files before copying — removes old hashed filenames
            if (Directory.Exists(iis.ReactSitePath))
            {
                foreach (var oldFile in Directory.GetFiles(iis.ReactSitePath, "*", SearchOption.AllDirectories))
                {
                    // Never delete web.config — IIS needs it for React Router
                    if (Path.GetFileName(oldFile).Equals("web.config", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { File.Delete(oldFile); } catch { }
                }
                _logger.LogInformation("Cleared old React files from {Path}", iis.ReactSitePath);
                log.AppendLine($"Cleared old files from {iis.ReactSitePath}");
            }
            // Source is the dist/build folder inside the staged react output
            var reactSrc = build.ReactOutputPath;
            _logger.LogInformation("React source folder: {Src}", reactSrc);
            log.AppendLine($"React source: {reactSrc}");

            var reactCopyOk = CopyDirectory(
                source: reactSrc,
                dest: iis.ReactSitePath,
                log);

            if (!reactCopyOk)
            {
                _logger.LogError("✖ React copy FAILED — rolling back");
                await RollbackAsync(backupPath, log, iis);
                await SetAppPoolStateAsync(log, iis, "start");
                return DeployResult.Fail(commit,
                    buildResult.FullLog + log.ToString(),
                    "Failed to copy React files");
            }

            _logger.LogInformation("✔ React files copied → {Path}", iis.ReactSitePath);
            log.AppendLine($"RESULT: React copy OK → {iis.ReactSitePath}");
            log.AppendLine();
        }
        else
        {
            _logger.LogWarning("ReactSitePath not configured — skipping React deploy");
        }

        // ── Step 5: Start app pool ─────────────────────────────
        await Progress(PipelineStage.Deploy, $"Starting IIS app pool {iis.AppPoolName}");
        _logger.LogInformation("▶ Starting IIS app pool: {Pool}", iis.AppPoolName);
        log.AppendLine("──── IIS pool start ────");

        var startOk = await SetAppPoolStateAsync(log, iis, "start");
        if (!startOk)
        {
            _logger.LogError("✖ Failed to start app pool — rolling back");
            await RollbackAsync(backupPath, log, iis);
            return DeployResult.Fail(commit,
                buildResult.FullLog + log.ToString(),
                "Failed to start IIS app pool after deploy");
        }

        // ── Step 6: Verify pool is running ────────────────────
        await Progress(PipelineStage.Verify, string.IsNullOrWhiteSpace(iis.HealthCheckUrl)
            ? "Verifying IIS application pool"
            : "Verifying IIS and health endpoint");
        await Task.Delay(TimeSpan.FromSeconds(3)); // give IIS a moment
        var isRunning = await VerifyDeploymentAsync(log, iis);

        if (!isRunning)
        {
            _logger.LogError("✖ App pool not running after start — rolling back");
            await RollbackAsync(backupPath, log, iis);
            return DeployResult.Fail(commit,
                buildResult.FullLog + log.ToString(),
                "App pool failed to start — rolled back");
        }

        // ── Done ───────────────────────────────────────────────
        var duration = DateTime.UtcNow - startedAt;
        _logger.LogInformation("✔✔ DEPLOY COMPLETE in {Seconds}s", (int)duration.TotalSeconds);
        _logger.LogInformation("🌐 Site is live at IIS pool: {Pool}", iis.AppPoolName);
        log.AppendLine($"Deploy completed in {(int)duration.TotalSeconds}s");

        return DeployResult.Ok(commit, buildResult.FullLog + log.ToString());
    }

    // ── IIS App Pool control ───────────────────────────────────

    private const string AppCmd = @"C:\Windows\System32\inetsrv\appcmd.exe";

    private async Task<bool> SetAppPoolStateAsync(StringBuilder log, IISSettings iis, string action)
    {
        var desiredState = action.Equals("stop", StringComparison.OrdinalIgnoreCase) ? "stopped" : "started";
        try
        {
            _logger.LogInformation("Changing IIS pool {Pool} state via appcmd: {Action}", iis.AppPoolName, action);
            var psi = new ProcessStartInfo
            {
                FileName = AppCmd,
                Arguments = $"{action} apppool /apppool.name:\"{iis.AppPoolName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start appcmd.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                log.AppendLine($"ERROR: Timed out after 45 seconds while waiting for IIS app pool '{iis.AppPoolName}' to {action}.");
                return false;
            }

            var output = await outputTask;
            var error = await errorTask;
            log.AppendLine(output);
            if (!string.IsNullOrWhiteSpace(error)) log.AppendLine(error);
            var succeeded = process.ExitCode == 0 || output.Contains("already", StringComparison.OrdinalIgnoreCase);
            if (succeeded) log.AppendLine($"App pool {desiredState}");
            else log.AppendLine($"ERROR: appcmd exited with code {process.ExitCode} while trying to {action} '{iis.AppPoolName}'.");
            return succeeded;
        }
        catch (Exception ex)
        {
            log.AppendLine($"ERROR trying to {action} IIS app pool '{iis.AppPoolName}': {ex.Message}");
            return false;
        }
    }

    private async Task<bool> VerifyDeploymentAsync(StringBuilder log, IISSettings iis)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AppCmd,
                Arguments = $"list apppool /name:\"{iis.AppPoolName}\" /text:state",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start appcmd.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0 || !output.Equals("Started", StringComparison.OrdinalIgnoreCase))
            {
                log.AppendLine($"App pool verification failed: {(string.IsNullOrWhiteSpace(error) ? output : error)}");
                return false;
            }

            log.AppendLine("App pool state: Started");
            _logger.LogInformation("App pool {Pool} state: Started", iis.AppPoolName);

            if (string.IsNullOrWhiteSpace(iis.HealthCheckUrl)) return true;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync(iis.HealthCheckUrl);
            log.AppendLine($"Health check returned HTTP {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.AppendLine($"Deployment verification error: {ex.Message}");
            return false;
        }
    }

    // ── File operations ────────────────────────────────────────

    private bool CreateBackup(string backupPath, StringBuilder log, IISSettings iis)
    {
        try
        {
            var apiExists = Directory.Exists(iis.SitePath);
            var frontendExists = !string.IsNullOrWhiteSpace(iis.ReactSitePath) && Directory.Exists(iis.ReactSitePath);
            if (!apiExists && !frontendExists)
            {
                log.AppendLine("No existing site files to back up");
                return false;
            }

            Directory.CreateDirectory(backupPath);
            var success = true;
            if (apiExists)
                success &= CopyDirectory(iis.SitePath, Path.Combine(backupPath, "api"), log, skipConfigurationFiles: false);
            if (frontendExists)
                success &= CopyDirectory(iis.ReactSitePath, Path.Combine(backupPath, "frontend"), log, skipConfigurationFiles: false);

            _logger.LogInformation("Backup created: {Path}", backupPath);
            return success;
        }
        catch (Exception ex)
        {
            log.AppendLine($"Backup error: {ex.Message}");
            return false;
        }
    }

    private async Task RollbackAsync(string backupPath, StringBuilder log, IISSettings iis)
    {
        _logger.LogWarning("⚠ ROLLING BACK to {Path}", backupPath);
        log.AppendLine($"ROLLBACK: restoring from {backupPath}");

        if (!Directory.Exists(backupPath))
        {
            log.AppendLine("ROLLBACK SKIPPED: no backup found");
            return;
        }

        try
        {
            RestoreBackup(Path.Combine(backupPath, "api"), iis.SitePath, log);
            if (!string.IsNullOrWhiteSpace(iis.ReactSitePath))
                RestoreBackup(Path.Combine(backupPath, "frontend"), iis.ReactSitePath, log);

            _logger.LogInformation("✔ Rollback complete");
            log.AppendLine("ROLLBACK: complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback failed!");
            log.AppendLine($"ROLLBACK FAILED: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private void RestoreBackup(string source, string destination, StringBuilder log)
    {
        if (!Directory.Exists(source)) return;
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        if (!CopyDirectory(source, destination, log, skipConfigurationFiles: false))
            throw new IOException($"Could not restore backup from {source}.");
    }

    private bool CopyDirectory(string source, string dest, StringBuilder log, bool skipConfigurationFiles = true)
    {
        try
        {
            if (!Directory.Exists(source))
            {
                log.AppendLine($"ERROR: Source folder not found: {source}");
                _logger.LogError("Source folder not found: {Path}", source);
                return false;
            }

            Directory.CreateDirectory(dest);

            // Copy all files
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                if (skipConfigurationFiles && Path.GetFileName(relative).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dest, relative)))
                {
                    log.AppendLine($"SKIPPED (config): {relative}");
                    continue;
                }
                var destFile = Path.Combine(dest, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                try
                {
                    File.Copy(file, destFile, overwrite: true);
                }
                catch (IOException ioEx)
                {
                    _logger.LogWarning("Skipping locked file {File}: {Msg}", relative, ioEx.Message);
                    log.AppendLine($"SKIPPED (locked): {relative}");
                }
            }

            var fileCount = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length;
            log.AppendLine($"Copied {fileCount} files: {source} → {dest}");
            _logger.LogInformation("Copied {Count} files → {Dest}", fileCount, dest);

            return true;
        }
        catch (UnauthorizedAccessException uaEx)
        {
            _logger.LogWarning("Permission denied — skipping: {Msg}", uaEx.Message);
            log.AppendLine($"SKIPPED (permission denied): {uaEx.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error copying directory");
            log.AppendLine($"ERROR copying files: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// React builds output to /dist (Vite) or /build (CRA).
    /// This finds whichever one exists after npm run build.
    /// </summary>
    private static string GetReactDistFolder(string reactProjectPath)
    {
        var dist = Path.Combine(reactProjectPath, "dist");   // Vite
        var build = Path.Combine(reactProjectPath, "build");  // CRA

        if (Directory.Exists(dist)) return dist;
        if (Directory.Exists(build)) return build;

        // Fallback — return dist and let error handling catch it
        return dist;
    }
}

// ── Result object ──────────────────────────────────────────────

public class DeployResult
{
    public bool Success { get; private set; }
    public string FullLog { get; private set; } = string.Empty;
    public string? ErrorMessage { get; private set; }
    public CommitDescriptor Commit { get; private set; } = null!;

    public static DeployResult Ok(CommitDescriptor commit, string log) => new()
    {
        Success = true,
        Commit = commit,
        FullLog = log,
    };

    public static DeployResult Fail(CommitDescriptor commit, string log, string error) => new()
    {
        Success = false,
        Commit = commit,
        FullLog = log,
        ErrorMessage = error,
    };
}
