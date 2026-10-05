using LocalGhost.Agent.Services;
using LocalGhost.Services;
using LocalGhost.Shared.Models;
using Models = LocalGhost.Shared.Models;

public sealed class Worker(
    ILogger<Worker> logger,
    IConfiguration configuration,
    BuildRunner buildRunner,
    IISDeployer iisDeployer,
    AgentEventSender sender) : BackgroundService
{
    private readonly int _maxConcurrency = Math.Clamp(configuration.GetValue("AgentMaxConcurrency", 2), 1, 8);
    private int _activeJobs;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("👻 LocalGhost multi-project agent started. Max concurrency: {Count}", _maxConcurrency);
        var heartbeatTask = RunHeartbeatAsync(cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var jobs = await sender.GetJobsAsync(_maxConcurrency, cancellationToken);
                    if (jobs.Count > 0)
                    {
                        logger.LogInformation("Received {Count} deployment job(s)", jobs.Count);
                        await Task.WhenAll(jobs.Select(job => ProcessJobAsync(job, cancellationToken)));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Unhandled error in multi-project deployment loop"); }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
        finally { try { await heartbeatTask; } catch (OperationCanceledException) { } }
    }

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await sender.SendHeartbeatAsync(new AgentHeartbeat
            {
                Timestamp = DateTime.UtcNow,
                Version = typeof(Worker).Assembly.GetName().Version?.ToString() ?? "unknown",
                ActiveJobs = Volatile.Read(ref _activeJobs)
            });
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

    private async Task ProcessJobAsync(ProjectDeploymentJob job, CancellationToken cancellationToken)
    {
        var project = job.Project;
        var commit = new CommitDescriptor
        {
            Sha = job.Commit.Sha,
            Author = job.Commit.Author,
            Message = job.Commit.Message,
            Branch = job.Commit.Branch,
            CommittedAt = job.Commit.CommittedAt
        };
        var record = new DeployRecord
        {
            Id = job.DeploymentId == Guid.Empty ? Guid.NewGuid() : job.DeploymentId,
            ProjectId = project.Id,
            ProjectName = project.Name,
            CommitSha = commit.Sha,
            CommitMessage = commit.Message,
            CommitAuthor = commit.Author,
            Branch = commit.Branch,
            CommittedAt = commit.CommittedAt,
            Status = DeployStatus.Running,
            Stage = PipelineStage.Build,
            StageMessage = "Starting build",
            StartedAt = job.StartedAt == default ? DateTime.UtcNow : job.StartedAt
        };

        Interlocked.Increment(ref _activeJobs);
        try
        {
            logger.LogInformation("🚀 {Project}: pipeline starting for {Sha}", project.Name, commit.ShortSha);
            await sender.SendProgressAsync(record);
            await PushLog(project.Id, record.Id, $"▶ {project.Name}: build started for {commit.ShortSha}", sender);

            var buildResult = await buildRunner.RunAsync(commit, project,
                line => PushLog(project.Id, record.Id, line, sender,
                    line.StartsWith("ERR", StringComparison.OrdinalIgnoreCase) ? Models.LogLevel.Error : Models.LogLevel.Info));

            if (!buildResult.Success)
            {
                record.Status = DeployStatus.Failed;
                record.ErrorMessage = buildResult.ErrorMessage;
                record.FailedStep = "Build";
                record.StageMessage = "Build failed";
                record.FinishedAt = DateTime.UtcNow;
                record.FullLog = buildResult.FullLog;
                await PushLog(project.Id, record.Id, $"✖ Build failed: {buildResult.ErrorMessage}", sender, Models.LogLevel.Error);
                await sender.SendFinishAsync(record);
                return;
            }

            record.Stage = PipelineStage.Deploy;
            record.StageMessage = "Preparing IIS deployment";
            await sender.SendProgressAsync(record);
            await PushLog(project.Id, record.Id, "▶ Build succeeded — starting IIS deployment...", sender, Models.LogLevel.Success);
            var deployResult = await iisDeployer.DeployAsync(buildResult, project, async (stage, message) =>
            {
                record.Stage = stage;
                record.StageMessage = message;
                await sender.SendProgressAsync(record);
                await PushLog(project.Id, record.Id, message, sender);
            });
            record.FinishedAt = DateTime.UtcNow;
            record.FullLog = deployResult.FullLog;

            if (!deployResult.Success)
            {
                record.Status = DeployStatus.Failed;
                record.ErrorMessage = deployResult.ErrorMessage;
                record.FailedStep = "Deploy";
                record.StageMessage = "Deployment failed";
                await PushLog(project.Id, record.Id, $"✖ Deploy failed: {deployResult.ErrorMessage}", sender, Models.LogLevel.Error);
            }
            else
            {
                record.Status = DeployStatus.Success;
                record.Stage = PipelineStage.Completed;
                record.StageMessage = "Deployment completed successfully";
                await PushLog(project.Id, record.Id, "🎉 Deployment complete — site is live!", sender, Models.LogLevel.Success);
            }

            await sender.SendFinishAsync(record);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            record.Status = DeployStatus.Cancelled;
            record.ErrorMessage = "Agent stopped during deployment.";
            record.FailedStep = "Agent";
            record.StageMessage = "Deployment cancelled";
            record.FinishedAt = DateTime.UtcNow;
            await sender.SendFinishAsync(record);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pipeline crashed for project {Project}", project.Name);
            record.Status = DeployStatus.Failed;
            record.ErrorMessage = ex.Message;
            record.FailedStep = "Agent";
            record.StageMessage = "Agent pipeline error";
            record.FinishedAt = DateTime.UtcNow;
            await PushLog(project.Id, record.Id, $"✖ Agent error: {ex.Message}", sender, Models.LogLevel.Error);
            await sender.SendFinishAsync(record);
        }
        finally
        {
            Interlocked.Decrement(ref _activeJobs);
        }
    }

    private static Task PushLog(Guid projectId, Guid deployId, string message, AgentEventSender sender,
        Models.LogLevel level = Models.LogLevel.Info) => sender.SendLogAsync(new LogEntry
        {
            ProjectId = projectId,
            DeployId = deployId,
            Message = message,
            Level = level,
            Timestamp = DateTime.UtcNow,
            Source = "Agent"
        });
}
