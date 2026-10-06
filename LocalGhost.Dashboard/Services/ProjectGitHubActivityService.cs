using System.Net.Http.Headers;
using System.Text.Json;
using LocalGhost.Dashboard.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace LocalGhost.Dashboard.Services;

public sealed record GitHubCommitView(string Sha, string Message, string Author, DateTimeOffset? CommittedAt, string Url);
public sealed record GitHubPullView(int Number, string Title, string Author, string State, bool Draft,
    string BaseBranch, string HeadBranch, DateTimeOffset UpdatedAt, string Url);

public sealed class ProjectGitHubActivityService(
    HttpClient http,
    IDbContextFactory<ProjectDbContext> dbFactory,
    ProjectAccessService access,
    GitHubCredentialService credentials,
    IMemoryCache cache)
{
    public async Task<List<GitHubCommitView>> GetCommitsAsync(string userId, Guid projectId, string branch)
    {
        var project = await GetProjectAsync(userId, projectId);
        if (!Branches(project).Contains(branch, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a configured deployment branch.");
        if (!IsConfigured(project)) return [];
        var key = $"github-commits:{userId}:{project.Id}:{project.UpdatedAt.Ticks}:{branch}";
        if (cache.TryGetValue(key, out List<GitHubCommitView>? cached) && cached is not null) return cached;
        var branches = Branches(project);
        var isPrimary = string.Equals(branch, branches[0], StringComparison.OrdinalIgnoreCase);
        var resource = isPrimary
            ? $"commits?sha={Uri.EscapeDataString(branch)}&per_page=100"
            : $"compare/{Uri.EscapeDataString(branches[0])}...{Uri.EscapeDataString(branch)}?per_page=100";
        using var document = await ReadAsync(project, resource);
        var result = ParseCommits(project, isPrimary ? document.RootElement : document.RootElement.GetProperty("commits"));
        if (!isPrimary)
        {
            var total = document.RootElement.GetProperty("total_commits").GetInt32();
            var lastPage = Math.Max(1, (int)Math.Ceiling(total / 100d));
            if (lastPage > 1)
            {
                using var last = await ReadAsync(project, resource + $"&page={lastPage}");
                result = ParseCommits(project, last.RootElement.GetProperty("commits"));
                if (result.Count < 100)
                {
                    if (lastPage == 2) result.InsertRange(0, ParseCommits(project, document.RootElement.GetProperty("commits")));
                    else
                    {
                        using var previous = await ReadAsync(project, resource + $"&page={lastPage - 1}");
                        result.InsertRange(0, ParseCommits(project, previous.RootElement.GetProperty("commits")));
                    }
                }
            }
            result = result.TakeLast(100).Reverse().ToList(); // Compare results are oldest first.
        }
        cache.Set(key, result, TimeSpan.FromSeconds(45));
        return result;
    }

    public async Task<List<GitHubPullView>> GetPullRequestsAsync(string userId, Guid projectId)
    {
        var project = await GetProjectAsync(userId, projectId);
        if (!IsConfigured(project)) return [];
        var key = $"github-pulls:{userId}:{project.Id}:{project.UpdatedAt.Ticks}";
        if (cache.TryGetValue(key, out List<GitHubPullView>? cached) && cached is not null) return cached;
        using var document = await ReadAsync(project, "pulls?state=all&sort=updated&direction=desc&per_page=20");
        var result = document.RootElement.EnumerateArray().Select(item =>
        {
            var number = item.GetProperty("number").GetInt32();
            var merged = item.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind != JsonValueKind.Null;
            return new GitHubPullView(number, item.GetProperty("title").GetString() ?? "Untitled PR",
                item.GetProperty("user").GetProperty("login").GetString() ?? "Unknown",
                merged ? "Merged" : item.GetProperty("state").GetString() == "open" ? "Open" : "Closed",
                item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
                item.GetProperty("base").GetProperty("ref").GetString() ?? "",
                item.GetProperty("head").GetProperty("ref").GetString() ?? "",
                DateTimeOffset.Parse(item.GetProperty("updated_at").GetString()!),
                $"https://github.com/{Uri.EscapeDataString(project.RepoOwner)}/{Uri.EscapeDataString(project.RepoName)}/pull/{number}");
        }).ToList();
        cache.Set(key, result, TimeSpan.FromSeconds(45));
        return result;
    }

    public static string[] Branches(ProjectEntity project) => project.Branch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    public static bool IsConfigured(ProjectEntity project) =>
        !string.IsNullOrWhiteSpace(project.SitePath) && !string.IsNullOrWhiteSpace(project.AppPoolName) &&
        !string.IsNullOrWhiteSpace(project.BackupPath) && !string.IsNullOrWhiteSpace(project.ApiProjectPath) &&
        !string.IsNullOrWhiteSpace(project.SourceCodePath);

    private static List<GitHubCommitView> ParseCommits(ProjectEntity project, JsonElement commits) =>
        commits.EnumerateArray().Select(item =>
        {
            var commit = item.GetProperty("commit");
            var author = commit.TryGetProperty("author", out var authorElement) && authorElement.ValueKind == JsonValueKind.Object
                ? authorElement : commit.GetProperty("committer");
            var sha = item.GetProperty("sha").GetString() ?? "";
            return new GitHubCommitView(sha, commit.GetProperty("message").GetString() ?? "",
                author.ValueKind == JsonValueKind.Object && author.TryGetProperty("name", out var name) ? name.GetString() ?? "Unknown" : "Unknown",
                author.ValueKind == JsonValueKind.Object && author.TryGetProperty("date", out var when) &&
                    DateTimeOffset.TryParse(when.GetString(), out var date) ? date : null,
                $"https://github.com/{Uri.EscapeDataString(project.RepoOwner)}/{Uri.EscapeDataString(project.RepoName)}/commit/{sha}");
        }).ToList();

    private async Task<ProjectEntity> GetProjectAsync(string userId, Guid projectId)
    {
        if (!await access.CanAsync(userId, projectId, ProjectMemberRole.Viewer))
            throw new UnauthorizedAccessException("Project access is required.");
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectId && !x.IsArchived);
    }

    private async Task<JsonDocument> ReadAsync(ProjectEntity project, string resource)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = await credentials.GetProjectTokenAsync(project, timeout.Token);
        var owner = Uri.EscapeDataString(project.RepoOwner);
        var repository = Uri.EscapeDataString(project.RepoName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repository}/{resource}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("LocalGhost-Dashboard/1.0");
        using var response = await http.SendAsync(request, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict && resource.StartsWith("commits?", StringComparison.Ordinal))
            return JsonDocument.Parse("[]"); // GitHub returns 409 for an empty repository.
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub activity is unavailable (HTTP {(int)response.StatusCode}). " +
                (resource.StartsWith("pulls?", StringComparison.Ordinal) ?
                    "For a private repository, the token needs Pull requests: Read permission." :
                    "For a private repository, the token needs Contents: Read permission."));
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
    }
}
