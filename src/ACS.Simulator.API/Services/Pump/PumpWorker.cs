namespace ACS.Simulator.API.Services.Pump;

/// <summary>Loop chính: enabled thì tick pump mỗi IntervalSeconds.</summary>
public sealed class PumpWorker(PumpService pump, ILogger<PumpWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = pump.GetSnapshot();
            try
            {
                if (snapshot.Enabled)
                    await pump.RunTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pump tick failed");
            }
            var delay = TimeSpan.FromSeconds(Math.Max(snapshot.IntervalSeconds, 3));
            pump.SetNextTick(DateTime.UtcNow + delay);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, pump.GetWakeToken());
            try
            {
                await Task.Delay(delay, linked.Token);
            }
            catch (OperationCanceledException)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;
                // Đổi config / bật-tắt pump → bỏ giấc cũ, lặp lại ngay với snapshot mới.
            }
        }
    }
}
