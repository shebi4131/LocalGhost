using LocalGhost.Shared.Models;
using Microsoft.Extensions.Options;
using Octokit;

namespace LocalGhost.Services;


public class GitPoller
{
    private readonly GitHubClient _github;
    private readonly GitHubSettings _settings;
    private readonly ILogger<GitPoller> _logger;

    // File on disk that stores the last deployed commit SHA
    // So if the Agent restarts, it remembers where it left off
    private readonly string _shaFilePath = "last_sha.txt";

    public GitPoller(IOptions<AppSettings> options, ILogger<GitPoller> logger)
    {
        _settings = options.Value.GitHub;
        _logger = logger;

        _github = new GitHubClient(new ProductHeaderValue("LocalGhost-Agent"))
        {
            Credentials = new Credentials(_settings.Token)
        };
    }

    /// <summary>
    /// Checks GitHub for a new commit on the configured branch.
    /// Returns the commit info if a new one is found, null if nothing changed.
    /// </summary>
    public async Task<CommitInfo?> CheckForNewCommitAsync()
    {
        try
        {
            // Get the latest commit on our branch from GitHub API
            var commits = await _github.Repository.Commit.GetAll(
                _settings.RepoOwner,
                _settings.RepoName,
                new CommitRequest { Sha = _settings.Branch }
            );

            if (commits == null || commits.Count == 0)
            {
                _logger.LogWarning("No commits found in repo {Owner}/{Repo}",
                    _settings.RepoOwner, _settings.RepoName);
                return null;
            }

            var latest = commits[0];
            var latestSha = latest.Sha;
            var lastSha = ReadLastSha();

            // Nothing new — same SHA as last time
            if (latestSha == lastSha)
            {
                _logger.LogInformation("No new commits. HEAD is still {Sha}",
                    latestSha[..7]);
                return null;
            }

            // New commit detected!
            _logger.LogInformation(
                "🆕 New commit detected! {ShortSha} by {Author} — \"{Message}\"",
                latestSha[..7],
                latest.Commit.Author.Name,
                TrimMessage(latest.Commit.Message));

            // Save this SHA so we don't deploy it again on next poll
            SaveLastSha(latestSha);

            return new CommitInfo
            {
                Sha = latestSha,
                Author = latest.Commit.Author.Name,
                Message = latest.Commit.Message,
                Branch = _settings.Branch,
                CommittedAt = latest.Commit.Author.Date.UtcDateTime
            };
        }
        catch (AuthorizationException)
        {
            _logger.LogError(
                "GitHub auth failed. Check your Token in appsettings.json");
            return null;
        }
        catch (NotFoundException)
        {
            _logger.LogError(
                "Repo {Owner}/{Repo} not found. Check RepoOwner and RepoName in appsettings.json",
                _settings.RepoOwner, _settings.RepoName);
            return null;
        }
        catch (RateLimitExceededException)
        {
            _logger.LogWarning(
                "GitHub API rate limit hit. Will retry next poll.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while polling GitHub");
            return null;
        }
    }

    // ── SHA persistence helpers ────────────────────────────────────

    private string? ReadLastSha()
    {
        if (!File.Exists(_shaFilePath))
            return null;

        var sha = File.ReadAllText(_shaFilePath).Trim();
        return string.IsNullOrEmpty(sha) ? null : sha;
    }

    private void SaveLastSha(string sha)
    {
        File.WriteAllText(_shaFilePath, sha);
        _logger.LogInformation("Saved new HEAD SHA: {Sha}", sha[..7]);
    }

    private static string TrimMessage(string message)
    {
        // Commit messages can be long — show only first line in logs
        var firstLine = message.Split('\n')[0].Trim();
        return firstLine.Length > 72
            ? firstLine[..72] + "..."
            : firstLine;
    }
}

/// <summary>
/// The commit data passed from GitPoller to Worker → BuildRunner → IISDeployer
/// </summary>
public class CommitInfo
{
    public string Sha { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public DateTime CommittedAt { get; set; }

    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}