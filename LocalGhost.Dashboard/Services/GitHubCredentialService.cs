using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using LocalGhost.Dashboard.Data;
using Microsoft.AspNetCore.DataProtection;

namespace LocalGhost.Dashboard.Services;

public sealed record GitHubRepositoryOption(long Id, string Owner, string Name, string DefaultBranch, bool IsPrivate);

// Single-user mode: Git Credential Manager owns the OAuth sign-in and Windows credential storage.
public sealed class GitHubCredentialService(HttpClient http, IDataProtectionProvider dataProtection)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("LocalGhost.ProjectSecrets.v1");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cacheUntil;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GetRepositoriesAsync(cancellationToken);
            return;
        }
        catch (InvalidOperationException) { }
        await RunGitAsync(["credential-manager", "github", "login", "--browser"], null,
            TimeSpan.FromMinutes(3), cancellationToken);
        _cachedToken = null;
        await GetRepositoriesAsync(cancellationToken);
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedToken is not null && _cacheUntil > DateTimeOffset.UtcNow) return _cachedToken;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken is not null && _cacheUntil > DateTimeOffset.UtcNow) return _cachedToken;
            var accounts = await RunGitAsync(["credential-manager", "github", "list", "--no-ui"], null,
                TimeSpan.FromSeconds(10), cancellationToken);
            foreach (var account in accounts.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string output;
                try
                {
                    output = await RunGitAsync(["credential-manager", "get", "--no-ui"],
                        $"protocol=https\nhost=github.com\nusername={account}\n\n", TimeSpan.FromSeconds(10), cancellationToken);
                }
                catch (InvalidOperationException) { continue; }
                var token = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => line.StartsWith("password=", StringComparison.Ordinal))?["password=".Length..];
                if (string.IsNullOrWhiteSpace(token)) continue;
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.UserAgent.ParseAdd("LocalGhost-Dashboard/1.0");
                using var response = await http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                _cachedToken = token;
                _cacheUntil = DateTimeOffset.UtcNow.AddMinutes(5);
                return token;
            }
            throw new InvalidOperationException("No usable GitHub credential was found for the Windows account running the Dashboard.");
        }
        finally { _gate.Release(); }
    }

    public async Task<List<GitHubRepositoryOption>> GetRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetTokenAsync(cancellationToken);
        var repositories = new List<GitHubRepositoryOption>();
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/user/repos?visibility=all&affiliation=owner,collaborator,organization_member&per_page=100&page={page}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("LocalGhost-Dashboard/1.0");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"GitHub could not list repositories (HTTP {(int)response.StatusCode}). Reconnect GitHub and try again.");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var items = json.RootElement;
            foreach (var item in items.EnumerateArray())
                repositories.Add(new GitHubRepositoryOption(item.GetProperty("id").GetInt64(),
                    item.GetProperty("owner").GetProperty("login").GetString() ?? string.Empty,
                    item.GetProperty("name").GetString() ?? string.Empty,
                    item.GetProperty("default_branch").GetString() ?? "main",
                    item.GetProperty("private").GetBoolean()));
            if (items.GetArrayLength() < 100) break;
        }
        return repositories.OrderBy(x => x.Owner).ThenBy(x => x.Name).ToList();
    }

    public async Task<GitHubRepositoryOption?> GetRepositoryAsync(long repositoryId, CancellationToken cancellationToken = default) =>
        (await GetRepositoriesAsync(cancellationToken)).FirstOrDefault(x => x.Id == repositoryId);

    public Task<string> GetProjectTokenAsync(ProjectEntity project, CancellationToken cancellationToken = default) =>
        project.GitHubUseCredentialManager ? GetTokenAsync(cancellationToken) :
        Task.FromResult(_protector.Unprotect(project.GitHubTokenProtected));

    private static async Task<string> RunGitAsync(IReadOnlyList<string> arguments, string? input,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git Credential Manager could not start.");
        if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("GitHub sign-in timed out. Run the Dashboard interactively as your Windows user and try again.");
        }
        var text = await output;
        var diagnostic = await error;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("GitHub sign-in is unavailable. Check Git Credential Manager and reconnect GitHub." +
                (diagnostic.Contains("No UI", StringComparison.OrdinalIgnoreCase) ? " The Dashboard must run under your interactive Windows account." : string.Empty));
        return text;
    }
}
