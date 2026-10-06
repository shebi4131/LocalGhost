using LocalGhost.Dashboard.Api;
using LocalGhost.Dashboard.Components;
using LocalGhost.Dashboard.Data;
using LocalGhost.Dashboard.Hubs;
using LocalGhost.Dashboard.Services;
using LocalGhost.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "LocalGhost Dashboard"); // ← add this
var keyDirectory = builder.Configuration["DataProtection:KeyDirectory"];
if (!string.IsNullOrWhiteSpace(keyDirectory))
{
    Directory.CreateDirectory(keyDirectory);
    var keys = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
    if (OperatingSystem.IsWindows()) keys.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

// ── Blazor ─────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Auth")));
builder.Services.AddDbContextFactory<ProjectDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Projects")));
builder.Services.AddIdentityCore<IdentityUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
    })
    .AddEntityFrameworkStores<AuthDbContext>()
    .AddSignInManager();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies(cookies =>
    {
        cookies.ApplicationCookie?.Configure(options =>
        {
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
        });
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// ── SignalR ─────────────────────────────────────────────────────
builder.Services.AddSignalR();

// ── State service (singleton — shared across all Blazor circuits)
builder.Services.AddSingleton<DeployStateService>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<ProjectAccessService>();
builder.Services.AddSingleton<ProjectWorkspaceService>();
builder.Services.AddSingleton<DeploymentApprovalService>();
builder.Services.AddSingleton<UserNotificationService>();
builder.Services.AddSingleton<AgentRequestAuthorizer>();
builder.Services.AddSingleton<ProjectSourceService>();
builder.Services.AddSingleton<ProjectNotificationService>();
builder.Services.AddSingleton<ProjectPreflightService>();
builder.Services.AddHttpClient<GitHubCredentialService>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<ProjectGitHubActivityService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHub<DeployHub>(DeployHub.Url);
app.MapDeployEvents();
app.MapAuthEndpoints();
app.MapProjectEndpoints();
app.MapGitHubCredentialEndpoints();
app.MapPathPickerEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.EnsureCreated();
    using var projectDb = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ProjectDbContext>>()
        .CreateDbContext();
    projectDb.Database.EnsureCreated();
    // Existing installations use EnsureCreated, so add this small column in place.
    {
        var connection = projectDb.Database.GetDbConnection();
        connection.Open();
        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info('DeploymentRuns')";
        using var reader = columns.ExecuteReader();
        var hasHiddenColumn = false;
        var hasCommittedAtColumn = false;
        while (reader.Read())
        {
            hasHiddenColumn |= string.Equals(reader.GetString(1), "IsHidden", StringComparison.OrdinalIgnoreCase);
            hasCommittedAtColumn |= string.Equals(reader.GetString(1), "CommittedAt", StringComparison.OrdinalIgnoreCase);
        }
        reader.Close();
        if (!hasHiddenColumn)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE DeploymentRuns ADD COLUMN IsHidden INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        if (!hasCommittedAtColumn)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE DeploymentRuns ADD COLUMN CommittedAt TEXT NULL";
            alter.ExecuteNonQuery();
        }
        using (var baselines = connection.CreateCommand())
        {
            baselines.CommandText = "CREATE TABLE IF NOT EXISTS ProjectBranchBaselines (ProjectId TEXT NOT NULL, Branch TEXT NOT NULL, CommitSha TEXT NOT NULL, PRIMARY KEY (ProjectId, Branch))";
            baselines.ExecuteNonQuery();
        }
        using (var columnsProject = connection.CreateCommand())
        {
            columnsProject.CommandText = "PRAGMA table_info('Projects')";
            using var projectReader = columnsProject.ExecuteReader();
            var hasLocalAuth = false;
            var hasRepository = false;
            var hasKind = false;
            var hasGroup = false;
            var hasApproval = false;
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (projectReader.Read())
            {
                existingColumns.Add(projectReader.GetString(1));
                hasLocalAuth |= string.Equals(projectReader.GetString(1), "GitHubUseCredentialManager", StringComparison.OrdinalIgnoreCase);
                hasRepository |= string.Equals(projectReader.GetString(1), "GitHubRepositoryId", StringComparison.OrdinalIgnoreCase);
                hasKind |= string.Equals(projectReader.GetString(1), "Kind", StringComparison.OrdinalIgnoreCase);
                hasGroup |= string.Equals(projectReader.GetString(1), "ProjectGroupId", StringComparison.OrdinalIgnoreCase);
                hasApproval |= string.Equals(projectReader.GetString(1), "RequiresApproval", StringComparison.OrdinalIgnoreCase);
            }
            projectReader.Close();
            if (!hasLocalAuth)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE Projects ADD COLUMN GitHubUseCredentialManager INTEGER NOT NULL DEFAULT 0";
                alter.ExecuteNonQuery();
            }
            if (!hasRepository)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE Projects ADD COLUMN GitHubRepositoryId INTEGER NULL";
                alter.ExecuteNonQuery();
            }
            if (!hasKind)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE Projects ADD COLUMN Kind INTEGER NOT NULL DEFAULT 0";
                alter.ExecuteNonQuery();
            }
            if (!hasGroup)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE Projects ADD COLUMN ProjectGroupId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'";
                alter.ExecuteNonQuery();
            }
            if (!hasApproval)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE Projects ADD COLUMN RequiresApproval INTEGER NOT NULL DEFAULT 0";
                alter.ExecuteNonQuery();
            }
            var migrationColumns = new Dictionary<string, string>
            {
                ["MigrationsEnabled"] = "INTEGER NOT NULL DEFAULT 0",
                ["MigrationProjectPath"] = "TEXT NOT NULL DEFAULT ''",
                ["MigrationStartupProjectPath"] = "TEXT NOT NULL DEFAULT ''",
                ["MigrationDbContextName"] = "TEXT NOT NULL DEFAULT ''",
                ["MigrationConnectionName"] = "TEXT NOT NULL DEFAULT 'DefaultConnection'",
                ["DatabaseConnectionProtected"] = "TEXT NOT NULL DEFAULT ''",
                ["SqlBackupPath"] = "TEXT NOT NULL DEFAULT ''",
                ["MigrationTimeoutMinutes"] = "INTEGER NOT NULL DEFAULT 10"
            };
            foreach (var (column, definition) in migrationColumns)
            {
                if (existingColumns.Contains(column)) continue;
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE Projects ADD COLUMN {column} {definition}";
                alter.ExecuteNonQuery();
            }
        }
        using (var groups = connection.CreateCommand())
        {
            groups.CommandText = "UPDATE Projects SET ProjectGroupId = Id WHERE ProjectGroupId = '00000000-0000-0000-0000-000000000000'";
            groups.ExecuteNonQuery();
        }
        using (var members = connection.CreateCommand())
        {
            members.CommandText = "CREATE TABLE IF NOT EXISTS ProjectMembers (ProjectGroupId TEXT NOT NULL, UserId TEXT NOT NULL, Role INTEGER NOT NULL, AddedAt TEXT NOT NULL, PRIMARY KEY (ProjectGroupId, UserId))";
            members.ExecuteNonQuery();
        }
        using (var grants = connection.CreateCommand())
        {
            grants.CommandText = "CREATE TABLE IF NOT EXISTS ProjectEnvironmentGrants (ProjectId TEXT NOT NULL, UserId TEXT NOT NULL, Role INTEGER NOT NULL, PRIMARY KEY (ProjectId, UserId))";
            grants.ExecuteNonQuery();
        }
        using (var invites = connection.CreateCommand())
        {
            invites.CommandText = "CREATE TABLE IF NOT EXISTS ProjectInvitations (Id TEXT NOT NULL PRIMARY KEY, ProjectGroupId TEXT NOT NULL, TokenHash TEXT NOT NULL, Role INTEGER NOT NULL, EnvironmentId TEXT NULL, CreatedAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL, RedeemedAt TEXT NULL, RedeemedByUserId TEXT NULL)";
            invites.ExecuteNonQuery();
        }
        using (var inviteIndex = connection.CreateCommand())
        {
            inviteIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_ProjectInvitations_TokenHash ON ProjectInvitations (TokenHash)";
            inviteIndex.ExecuteNonQuery();
        }
        using (var approvals = connection.CreateCommand())
        {
            approvals.CommandText = "CREATE TABLE IF NOT EXISTS DeploymentApprovals (Id TEXT NOT NULL PRIMARY KEY, ProjectId TEXT NOT NULL, Branch TEXT NOT NULL, CommitSha TEXT NOT NULL, RequestedByUserId TEXT NOT NULL, DecidedByUserId TEXT NULL, RequestedAt TEXT NOT NULL, DecidedAt TEXT NULL, ConsumedAt TEXT NULL, Approved INTEGER NULL)";
            approvals.ExecuteNonQuery();
        }
        using (var notifications = connection.CreateCommand())
        {
            notifications.CommandText = "CREATE TABLE IF NOT EXISTS UserNotifications (Id TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL, ProjectId TEXT NOT NULL, EventKey TEXT NOT NULL, Kind TEXT NOT NULL, Title TEXT NOT NULL, Message TEXT NOT NULL, Url TEXT NOT NULL, CreatedAt TEXT NOT NULL, ReadAt TEXT NULL)";
            notifications.ExecuteNonQuery();
        }
        using (var notificationIndex = connection.CreateCommand())
        {
            notificationIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_UserNotifications_UserId_EventKey ON UserNotifications (UserId, EventKey); CREATE INDEX IF NOT EXISTS IX_UserNotifications_UserId_CreatedAt ON UserNotifications (UserId, CreatedAt)";
            notificationIndex.ExecuteNonQuery();
        }
        connection.Close();
    }

    // In development, dashboard and agent are restarted together by Visual Studio.
    // Recover runs that were interrupted by Stop/Restart instead of waiting for their lease to expire.
    if (app.Environment.IsDevelopment())
    {
        var interrupted = projectDb.DeploymentRuns.Where(x => x.Status == (int)DeployStatus.Running).ToList();
        foreach (var run in interrupted)
        {
            run.Status = (int)DeployStatus.Failed;
            run.FinishedAt = DateTime.UtcNow;
            run.FailedStep ??= "Agent interrupted";
            run.ErrorMessage ??= "The development dashboard or agent stopped before this deployment completed.";
        }

        foreach (var project in projectDb.Projects.Where(x => x.IsDeploymentInProgress))
        {
            project.IsDeploymentInProgress = false;
            project.AgentLeaseUntil = null;
        }

        projectDb.SaveChanges();
    }
}

app.Run();
