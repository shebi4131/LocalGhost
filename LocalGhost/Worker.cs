using LocalGhost.Services;
using LocalGhost.Shared.Models;
using Microsoft.Extensions.Options;

public class Worker(
    ILogger<Worker> logger,
    IOptions<AppSettings> options,
    GitPoller gitPoller) : BackgroundService
{
    private readonly AppSettings _settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation(
            "👻 LocalGhost Agent started. Polling every {Interval}s on branch '{Branch}'",
            _settings.PollIntervalSeconds,
            _settings.GitHub.Branch);

        while (!ct.IsCancellationRequested)
        {
            logger.LogInformation("[{Time}] Checking for new commits...",
                DateTime.Now.ToString("HH:mm:ss"));

            try
            {
                // ── Day 2: Detect new commit ──────────────────────
                var commit = await gitPoller.CheckForNewCommitAsync();

                if (commit is not null)
                {
                    logger.LogInformation(
                        "🚀 Starting deploy pipeline for {ShortSha}...",
                        commit.ShortSha);

                    // Day 3 → BuildRunner.RunAsync(commit) goes here
                    // Day 4 → IISDeployer.DeployAsync()   goes here
                    // Day 5 → NotificationService.Notify() goes here
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in deploy loop");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_settings.PollIntervalSeconds), ct);
        }
    }
}