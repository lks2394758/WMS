namespace Wms.Worker;

/// <summary>
/// M1 placeholder: proves the Windows Service host runs. In later milestones this writes the heartbeat row
/// checked by /health/ready and runs idempotency and refresh-token cleanup (guide §8.1, §11.4).
/// </summary>
public sealed partial class HeartbeatWorker(ILogger<HeartbeatWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            LogHeartbeat(logger, DateTimeOffset.UtcNow);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker heartbeat at {UtcNow:O}")]
    private static partial void LogHeartbeat(ILogger logger, DateTimeOffset utcNow);
}
