using LocalGhost.Shared.Models;
using Microsoft.Extensions.Options;

public class Worker(
    ILogger<Worker> logger,
    IOptions<AppSettings> options) : BackgroundService
{
    private readonly AppSettings _settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("👻 LocalGhost Agent started. Polling every {Interval}s",
            _settings.PollIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("[{Time}] Checking for new commits...",
                    DateTime.Now.ToString("HH:mm:ss"));

                // Day 2 → GitPoller goes here
                // Day 3 → BuildRunner goes here
                // Day 4 → IISDeployer goes here
                // Day 5 → NotificationService goes here
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