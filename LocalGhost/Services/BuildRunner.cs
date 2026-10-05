using LocalGhost.Shared.Models;
using System.Diagnostics;
using System.Text;

namespace LocalGhost.Services;

public class BuildRunner
{
    private readonly ILogger<BuildRunner> _logger;

    public BuildRunner(ILogger<BuildRunner> logger) => _logger = logger;

    public async Task<BuildResult> RunAsync(CommitDescriptor commit, ProjectConfiguration project,
        Func<string, Task>? onLogLine = null)
    {
        var settings = project.Build;
        var log = new StringBuilder();
        var startedAt = DateTime.UtcNow;

        _logger.LogInformation("═══════════════════════════════════════");
        _logger.LogInformation("BUILD STARTED for commit {Sha}", commit.ShortSha);
        _logger.LogInformation("Author:  {Author}", commit.Author);
        _logger.LogInformation("Message: {Message}", commit.Message);
        _logger.LogInformation("═══════════════════════════════════════");

        log.AppendLine($"Build started at {startedAt:yyyy-MM-dd HH:mm:ss} UTC");
        log.AppendLine($"Commit : {commit.ShortSha} by {commit.Author}");
        log.AppendLine($"Message: {commit.Message}");
        log.AppendLine();

        _logger.LogInformation("✔ Source prepared by dashboard → {Path}", settings.SourceCodePath);
        log.AppendLine($"RESULT: source ready → {settings.SourceCodePath}");
        log.AppendLine();

        // ── Step 2: dotnet publish ─────────────────────────────
        if (!string.IsNullOrWhiteSpace(settings.ApiProjectPath))
        {
            // ── Clean first to avoid locked DLL issues ─────────
            _logger.LogInformation("▶ Running dotnet clean...");
            log.AppendLine("──── dotnet clean ────");

            await RunProcessAsync(
                fileName: "dotnet",
                arguments: $"clean \"{settings.ApiProjectPath}\" -c Release --nologo",
                workingDir: Path.GetDirectoryName(settings.ApiProjectPath)!,
                log: log,
                onLogLine: onLogLine,
                timeoutMinutes: settings.TimeoutMinutes);

            _logger.LogInformation("▶ Running dotnet publish...");
            log.AppendLine("──── dotnet publish ────");

            var apiResult = await RunProcessAsync(
                fileName: "dotnet",
                arguments: $"publish \"{settings.ApiProjectPath}\" -c Release -o \"{settings.ApiOutputPath}\" --nologo",
                workingDir: Path.GetDirectoryName(settings.ApiProjectPath)!,
                log: log,
                onLogLine: onLogLine,
                timeoutMinutes: settings.TimeoutMinutes);

            if (!apiResult)
            {
                _logger.LogError("✖ dotnet publish FAILED");
                log.AppendLine("RESULT: dotnet publish FAILED");
                return BuildResult.Fail(commit, log.ToString(), "dotnet publish failed");
            }

            _logger.LogInformation("✔ dotnet publish succeeded → {Path}", settings.ApiOutputPath);
            log.AppendLine($"RESULT: dotnet publish OK → {settings.ApiOutputPath}");
            log.AppendLine();
        }
        else
        {
            _logger.LogWarning("ApiProjectPath is empty — skipping dotnet publish");
            log.AppendLine("dotnet publish SKIPPED (ApiProjectPath not configured)");
        }

        // ── Step 3: npm install + npm run build ────────────────
        if (!string.IsNullOrWhiteSpace(settings.ReactProjectPath))
        {
            // npm install first — node_modules not in git repo
            _logger.LogInformation("▶ Running npm install...");
            log.AppendLine("──── npm install ────");

            var installResult = await RunProcessAsync(
                fileName: "cmd.exe",
                arguments: "/c npm install",
                workingDir: settings.ReactProjectPath,
                log: log,
                onLogLine: onLogLine,
                timeoutMinutes: settings.TimeoutMinutes);

            if (!installResult)
            {
                _logger.LogError("✖ npm install FAILED");
                log.AppendLine("RESULT: npm install FAILED");
                return BuildResult.Fail(commit, log.ToString(), "npm install failed");
            }

            _logger.LogInformation("✔ npm install succeeded");
            log.AppendLine("RESULT: npm install OK");
            log.AppendLine();

            // Now build
            _logger.LogInformation("▶ Running npm run build...");
            log.AppendLine("──── npm run build ────");

            var npmResult = await RunProcessAsync(
                fileName: "cmd.exe",
                arguments: "/c npm run build",
                workingDir: settings.ReactProjectPath,
                log: log,
                onLogLine: onLogLine,
                timeoutMinutes: settings.TimeoutMinutes);

            if (!npmResult)
            {
                _logger.LogError("✖ npm run build FAILED");
                log.AppendLine("RESULT: npm run build FAILED");
                return BuildResult.Fail(commit, log.ToString(), "npm run build failed");
            }

            _logger.LogInformation("✔ npm run build succeeded → {Path}", settings.ReactOutputPath);
            log.AppendLine($"RESULT: npm run build OK → {settings.ReactOutputPath}");
            log.AppendLine();
        }
        else
        {
            _logger.LogWarning("ReactProjectPath is empty — skipping npm build");
            log.AppendLine("npm build SKIPPED (ReactProjectPath not configured)");
        }

        // ── Done ───────────────────────────────────────────────
        var duration = DateTime.UtcNow - startedAt;
        _logger.LogInformation("✔✔ BUILD COMPLETE in {Seconds}s", (int)duration.TotalSeconds);
        log.AppendLine($"Build completed in {(int)duration.TotalSeconds}s");

        return BuildResult.Ok(commit, log.ToString());
    }

    // ── Core process runner ────────────────────────────────────

    private async Task<bool> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDir,
        StringBuilder log,
        Func<string, Task>? onLogLine = null,
        int timeoutMinutes = 10)
    {
        var displayArgs = arguments;
        _logger.LogInformation("CMD: {FileName} {Args}", fileName, displayArgs);
        log.AppendLine($"> {fileName} {displayArgs}");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };

        // Capture stdout line by line in real-time
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _logger.LogInformation("  {Line}", e.Data);
            log.AppendLine(e.Data);
            // Fire callback synchronously — streams each line to Dashboard live
            if (onLogLine is not null)
                _ = Task.Run(() => onLogLine(e.Data));
        };

        // Capture stderr (warnings + errors)
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _logger.LogWarning("  ERR: {Line}", e.Data);
            log.AppendLine($"ERR: {e.Data}");
            if (onLogLine is not null)
                Task.Run(() => onLogLine($"ERR: {e.Data}")).GetAwaiter().GetResult();
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            var message = $"Command timed out after {timeoutMinutes} minute(s).";
            log.AppendLine(message);
            _logger.LogError("{Command} {Message}", fileName, message);
            if (onLogLine is not null) await onLogLine($"ERR: {message}");
            return false;
        }

        var exitCode = process.ExitCode;
        log.AppendLine($"Exit code: {exitCode}");

        return exitCode == 0;
    }
}

// ── Result object ──────────────────────────────────────────────

public class BuildResult
{
    public bool Success { get; private set; }
    public string FullLog { get; private set; } = string.Empty;
    public string? ErrorMessage { get; private set; }
    public CommitDescriptor Commit { get; private set; } = null!;

    public static BuildResult Ok(CommitDescriptor commit, string log) => new()
    {
        Success = true,
        Commit = commit,
        FullLog = log,
    };

    public static BuildResult Fail(CommitDescriptor commit, string log, string error) => new()
    {
        Success = false,
        Commit = commit,
        FullLog = log,
        ErrorMessage = error,
    };
}
