using LocalGhost.Dashboard.Services;

namespace LocalGhost.Dashboard.Api;

public static class GitHubCredentialEndpoints
{
    public static void MapGitHubCredentialEndpoints(this WebApplication app) =>
        app.MapGet("/github/connect", ConnectAsync).RequireAuthorization();

    private static async Task<IResult> ConnectAsync(HttpContext context, GitHubCredentialService github, string? returnTo)
    {
        var target = returnTo is not null && returnTo.StartsWith("/projects/", StringComparison.Ordinal) &&
            !returnTo.StartsWith("//", StringComparison.Ordinal) ? returnTo : "/projects/new";
        try
        {
            await github.ConnectAsync(context.RequestAborted);
            return Results.Redirect($"/github/repositories?returnTo={Uri.EscapeDataString(target)}&connected=true");
        }
        catch (Exception)
        {
            return Results.Redirect($"/github/repositories?returnTo={Uri.EscapeDataString(target)}&error=GitHub%20sign-in%20failed.%20Run%20the%20Dashboard%20interactively%20as%20your%20Windows%20user%20and%20try%20again.");
        }
    }
}
