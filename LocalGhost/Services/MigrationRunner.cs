using System.Data.Common;
using System.Diagnostics;
using System.Text;
using LocalGhost.Shared.Models;

namespace LocalGhost.Services;

public sealed class MigrationRunner(ILogger<MigrationRunner> logger)
{
    public async Task<MigrationResult> RunAsync(ProjectConfiguration project, Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        var settings = project.Migration;
        if (!settings.Enabled) return MigrationResult.Ok("Migrations disabled.", null);
        if (string.IsNullOrWhiteSpace(settings.ConnectionString) || string.IsNullOrWhiteSpace(settings.ProjectPath) ||
            string.IsNullOrWhiteSpace(settings.SqlBackupPath))
            return MigrationResult.Fail("Migration project, SQL connection, or SQL backup path is missing.");

        var connection = ParseConnection(settings.ConnectionString);
        if (connection is null) return MigrationResult.Fail("SQL Server connection must specify Server and Database, plus integrated security or a SQL login.");
        if (!Path.IsPathFullyQualified(settings.SqlBackupPath))
            return MigrationResult.Fail("SQL backup path must be an absolute path on the SQL Server machine.");
        if (!File.Exists(settings.ProjectPath)) return MigrationResult.Fail($"Migrations project not found: {settings.ProjectPath}");
        var startup = string.IsNullOrWhiteSpace(settings.StartupProjectPath) ? settings.ProjectPath : settings.StartupProjectPath;
        if (!File.Exists(startup)) return MigrationResult.Fail($"Migration startup project not found: {startup}");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"localghost-migrations-{Guid.NewGuid():N}.sql");
        var databaseName = connection.Value.Database;
        var backupName = $"{project.Name}-{project.Environment}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.bak";
        foreach (var invalid in Path.GetInvalidFileNameChars()) backupName = backupName.Replace(invalid, '-');
        var backupPath = Path.Combine(settings.SqlBackupPath, backupName);
        var backupVerified = false;
        var safeDatabase = databaseName.Replace("]", "]]");
        var safeBackup = backupPath.Replace("'", "''");
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["DOTNET_ENVIRONMENT"] = "Production",
            [$"ConnectionStrings__{settings.ConnectionName}"] = settings.ConnectionString
        };

        try
        {
            var workingDirectory = Path.GetDirectoryName(startup)!;
            var toolManifest = FindToolManifest(workingDirectory);
            if (toolManifest is not null)
            {
                await log($"▶ Restoring project-local .NET tools from {toolManifest}");
                if (!await RunAsync("dotnet", ["tool", "restore"], workingDirectory,
                    new Dictionary<string, string>(), settings.TimeoutMinutes, log, cancellationToken))
                    return MigrationResult.Fail("Could not restore the project's .NET tool manifest. Check NuGet access and the dotnet-ef version in the repository; no database changes were made.");
            }
            await log("▶ Generating idempotent EF Core migration script");
            var efArgs = new List<string> { "ef", "migrations", "script", "--idempotent", "--project", settings.ProjectPath,
                "--startup-project", startup, "--output", scriptPath };
            if (!string.IsNullOrWhiteSpace(settings.DbContextName))
            {
                efArgs.Add("--context");
                efArgs.Add(settings.DbContextName);
            }
            if (!await RunAsync("dotnet", efArgs, workingDirectory, environment, settings.TimeoutMinutes, log, cancellationToken))
                return MigrationResult.Fail("EF Core could not generate the migration script. Check dotnet-ef, the project, and DbContext settings.");
            if (!File.Exists(scriptPath)) return MigrationResult.Fail("EF Core did not produce a migration script.");

            await log($"▶ Backing up SQL Server database {databaseName}");
            var backupSql = $"BACKUP DATABASE [{safeDatabase}] TO DISK = N'{safeBackup}' WITH COPY_ONLY, INIT, CHECKSUM; RESTORE VERIFYONLY FROM DISK = N'{safeBackup}' WITH CHECKSUM;";
            if (!await RunSqlcmdAsync(connection.Value, backupSql, null, settings.TimeoutMinutes, log, cancellationToken))
                return MigrationResult.Fail("SQL Server backup or verification failed; no migration was applied.");
            backupVerified = true;
            await log($"✓ SQL Server backup verified: {backupPath}");

            await log("▶ Applying pending migrations to SQL Server");
            if (!await RunSqlcmdAsync(connection.Value, null, scriptPath, settings.TimeoutMinutes, log, cancellationToken))
                return MigrationResult.Fail($"Migration failed. IIS files were not changed. Database backup: {backupPath}", backupPath);
            await log("✓ SQL Server migrations completed");
            return MigrationResult.Ok("Migrations applied successfully.", backupPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Migration failed for project {ProjectId}", project.Id);
            return MigrationResult.Fail(backupVerified
                ? $"Migration stage failed after backup. IIS files were not changed. Database backup: {backupPath}. {ex.Message}"
                : $"Migration stage failed before a verified backup: {ex.Message}",
                backupVerified ? backupPath : null);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private static string? FindToolManifest(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var direct = Path.Combine(current.FullName, "dotnet-tools.json");
            if (File.Exists(direct)) return direct;
            var standard = Path.Combine(current.FullName, ".config", "dotnet-tools.json");
            if (File.Exists(standard)) return standard;
        }
        return null;
    }

    private static (string Server, string Database, string? User, string? Password, bool TrustCertificate)? ParseConnection(string value)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = value };
        string? Get(params string[] keys) => keys.Select(key => builder.TryGetValue(key, out var item) ? item?.ToString() : null)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        var server = Get("Server", "Data Source", "Address", "Addr", "Network Address");
        var database = Get("Database", "Initial Catalog");
        var trusted = Get("Integrated Security", "Trusted_Connection");
        var integrated = string.Equals(trusted, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trusted, "sspi", StringComparison.OrdinalIgnoreCase) || trusted == "yes";
        var user = integrated ? null : Get("User ID", "UID", "User");
        var password = integrated ? null : Get("Password", "PWD");
        var trust = string.Equals(Get("TrustServerCertificate", "Trust Server Certificate"), "true", StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database) || (!integrated && string.IsNullOrWhiteSpace(user))
            ? null : (server, database, user, password, trust);
    }

    private static Task<bool> RunSqlcmdAsync((string Server, string Database, string? User, string? Password, bool TrustCertificate) connection,
        string? query, string? file, int timeoutMinutes, Func<string, Task> log, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-b", "-S", connection.Server, "-d", connection.Database };
        if (connection.TrustCertificate) args.Add("-C");
        if (connection.User is null) args.Add("-E");
        else { args.Add("-U"); args.Add(connection.User); }
        if (query is not null) { args.Add("-Q"); args.Add(query); }
        if (file is not null) { args.Add("-i"); args.Add(file); }
        var environment = new Dictionary<string, string>();
        if (connection.Password is not null) environment["SQLCMDPASSWORD"] = connection.Password;
        return RunAsync("sqlcmd", args, AppContext.BaseDirectory, environment, timeoutMinutes, log, cancellationToken);
    }

    private static async Task<bool> RunAsync(string executable, IReadOnlyList<string> args, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, int timeoutMinutes, Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var (key, value) in environment) start.Environment[key] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(timeoutMinutes, 1, 120)));
        async Task StreamAsync(StreamReader reader, bool error)
        {
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
                await log(error ? $"ERR: {line}" : line);
        }
        try
        {
            await Task.WhenAll(StreamAsync(process.StandardOutput, false), StreamAsync(process.StandardError, true),
                process.WaitForExitAsync(timeout.Token));
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await log("ERR: Migration command timed out or was cancelled.");
            return false;
        }
    }
}

public sealed record MigrationResult(bool Success, string Message, string? DatabaseBackupPath)
{
    public static MigrationResult Ok(string message, string? backup) => new(true, message, backup);
    public static MigrationResult Fail(string message, string? backup = null) => new(false, message, backup);
}
