using LocalGhost.Dashboard.Models;
using LocalGhost.Dashboard.Services;
using LocalGhost.Shared.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace LocalGhost.Dashboard.Api;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        app.MapPost("/projects/create", CreateAsync).RequireAuthorization();
        app.MapPost("/projects/{projectId:guid}/update", UpdateAsync).RequireAuthorization();
        app.MapPost("/projects/{projectId:guid}/state", SetStateAsync).RequireAuthorization();
        app.MapPost("/projects/{projectId:guid}/deploy", DeployAsync).RequireAuthorization();
    }

    private static async Task<IResult> CreateAsync(HttpContext context, IAntiforgery antiforgery,
        UserManager<IdentityUser> users, ProjectService projects)
    {
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync();
        var model = FromForm(form);
        var error = Validate(model, requireGitHubToken: true);
        if (error is not null) return RedirectError(NewProjectPath(model), error);
        var ownerId = users.GetUserId(context.User);
        if (ownerId is null) return Results.Redirect("/login");
        Guid id;
        try { id = await projects.CreateAsync(ownerId, model); }
        catch (InvalidOperationException ex) { return RedirectError(NewProjectPath(model), ex.Message); }
        return Results.Redirect($"/projects/{id}?created=true");
    }

    private static async Task<IResult> UpdateAsync(Guid projectId, HttpContext context, IAntiforgery antiforgery,
        UserManager<IdentityUser> users, ProjectService projects)
    {
        await antiforgery.ValidateRequestAsync(context);
        var model = FromForm(await context.Request.ReadFormAsync());
        var error = Validate(model, requireGitHubToken: false);
        if (error is not null) return RedirectError(SettingsPath(projectId, model), error);
        var ownerId = users.GetUserId(context.User);
        if (ownerId is null) return Results.Redirect("/login");
        try
        {
            if (!await projects.UpdateAsync(ownerId, projectId, model)) return Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return RedirectError(SettingsPath(projectId, model), ex.Message);
        }
        return Results.Redirect($"/projects/{projectId}/settings?saved=true");
    }

    private static async Task<IResult> SetStateAsync(Guid projectId, HttpContext context, IAntiforgery antiforgery,
        UserManager<IdentityUser> users, ProjectService projects)
    {
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync();
        var active = string.Equals(form["active"], "true", StringComparison.OrdinalIgnoreCase);
        var ownerId = users.GetUserId(context.User);
        if (ownerId is null || !await projects.SetActiveAsync(ownerId, projectId, active)) return Results.NotFound();
        return Results.Redirect($"/projects/{projectId}");
    }

    private static async Task<IResult> DeployAsync(Guid projectId, HttpContext context, IAntiforgery antiforgery,
        UserManager<IdentityUser> users, ProjectService projects, ProjectSourceService source,
        ProjectPreflightService preflight)
    {
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync();
        var ownerId = users.GetUserId(context.User);
        if (ownerId is null) return Results.Redirect("/login");
        var report = await preflight.RunAsync(ownerId, projectId, context.RequestAborted);
        if (report is null) return Results.NotFound();
        if (!report.CanDeploy)
        {
            var firstFailure = report.Checks.First(x => x.Status == PreflightCheckStatus.Failed);
            return RedirectError($"/projects/{projectId}", $"Preflight blocked deployment: {firstFailure.Name} — {firstFailure.Detail}");
        }
        if (!await projects.RequestDeploymentAsync(ownerId, projectId))
            return RedirectError($"/projects/{projectId}", "Activate the project before requesting a deployment.");
        source.QueueManualBranch(projectId, Text(form, "branch"));
        return Results.Redirect($"/projects/{projectId}?queued=true");
    }

    private static ProjectEditModel FromForm(IFormCollection form) => new()
    {
        Name = Text(form, "name"), Description = Text(form, "description"), Environment = Text(form, "environment"),
        Kind = Enum.TryParse<ProjectKind>(Text(form, "projectKind"), out var kind) && Enum.IsDefined(kind) ? kind : ProjectKind.AspNet,
        IsActive = Checked(form, "isActive"), PollIntervalSeconds = Number(Text(form, "pollIntervalSeconds"), 30),
        RepoOwner = Text(form, "repoOwner"), RepoName = Text(form, "repoName"), Branch = string.Join(",", new[] { Text(form, "branch"), Text(form, "newBranch") }.Where(value => !string.IsNullOrWhiteSpace(value))), GitHubToken = Text(form, "githubToken"),
        GitHubAuthMode = Text(form, "githubAuthMode"), GitHubRepositoryId = Long(Text(form, "githubRepositoryId")),
        SourceCodePath = Text(form, "sourceCodePath"), ApiProjectPath = Text(form, "apiProjectPath"), ReactProjectPath = Text(form, "reactProjectPath"),
        ApiOutputPath = Text(form, "apiOutputPath"), ReactOutputPath = Text(form, "reactOutputPath"), BuildTimeoutMinutes = Number(Text(form, "buildTimeoutMinutes"), 10),
        SitePath = Text(form, "sitePath"), ReactSitePath = Text(form, "reactSitePath"), AppPoolName = Text(form, "appPoolName"),
        BackupPath = Text(form, "backupPath"), HealthCheckUrl = Text(form, "healthCheckUrl"),
        EmailEnabled = Checked(form, "emailEnabled"), SmtpHost = Text(form, "smtpHost"), SmtpPort = Number(Text(form, "smtpPort"), 587),
        SmtpUsername = Text(form, "smtpUsername"), SmtpPassword = Text(form, "smtpPassword"), SmtpFromAddress = Text(form, "smtpFromAddress"),
        SmtpToAddress = Text(form, "smtpToAddress"), SmtpEnableSsl = Checked(form, "smtpEnableSsl"),
        NotifyOnSuccess = Checked(form, "notifyOnSuccess"), NotifyOnFailure = Checked(form, "notifyOnFailure")
    };

    private static string? Validate(ProjectEditModel model, bool requireGitHubToken)
    {
        if (string.IsNullOrWhiteSpace(model.Name)) return "Project name is required.";
        if (string.IsNullOrWhiteSpace(model.RepoOwner) || string.IsNullOrWhiteSpace(model.RepoName) || string.IsNullOrWhiteSpace(model.Branch))
            return "Repository owner, repository name, and branch are required.";
        if (model.GitHubAuthMode == "Local" && model.GitHubRepositoryId is null)
            return "Choose a repository from your connected GitHub account.";
        if (model.GitHubAuthMode != "Local" && requireGitHubToken && string.IsNullOrWhiteSpace(model.GitHubToken))
            return "A GitHub token is required for PAT fallback.";
        if (string.IsNullOrWhiteSpace(model.SourceCodePath)) return "A source checkout path is required.";
        if (string.IsNullOrWhiteSpace(model.ApiProjectPath) || string.IsNullOrWhiteSpace(model.ApiOutputPath) || string.IsNullOrWhiteSpace(model.SitePath))
            return "The .NET project, publish output, and IIS site paths are required.";
        if (model.Kind != ProjectKind.BlazorWebApp && !string.IsNullOrWhiteSpace(model.ReactProjectPath) && (string.IsNullOrWhiteSpace(model.ReactOutputPath) || string.IsNullOrWhiteSpace(model.ReactSitePath)))
            return "React output and IIS frontend site paths are required when a React project is configured.";
        if (string.IsNullOrWhiteSpace(model.AppPoolName) || string.IsNullOrWhiteSpace(model.BackupPath))
            return "IIS application pool and backup paths are required.";
        if (model.EmailEnabled && (string.IsNullOrWhiteSpace(model.SmtpHost) || string.IsNullOrWhiteSpace(model.SmtpFromAddress) || string.IsNullOrWhiteSpace(model.SmtpToAddress)))
            return "SMTP host, sender, and recipient are required when email notifications are enabled.";
        return null;
    }

    private static string Text(IFormCollection form, string key) => form[key].ToString();
    private static bool Checked(IFormCollection form, string key) =>
        string.Equals(Text(form, key), "on", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Text(form, key), "true", StringComparison.OrdinalIgnoreCase);
    private static int Number(string? value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;
    private static long? Long(string? value) => long.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    private static string NewProjectPath(ProjectEditModel model) => WithSelectedRepository("/projects/new", model);
    private static string SettingsPath(Guid projectId, ProjectEditModel model) => WithSelectedRepository($"/projects/{projectId}/settings", model);
    private static string WithSelectedRepository(string path, ProjectEditModel model) =>
        model.GitHubAuthMode == "Local" && model.GitHubRepositoryId is long repository
            ? $"{path}?repositoryId={repository}"
            : path;
    private static IResult RedirectError(string path, string message) => Results.Redirect($"{path}{(path.Contains('?') ? '&' : '?')}error={Uri.EscapeDataString(message)}");
}
