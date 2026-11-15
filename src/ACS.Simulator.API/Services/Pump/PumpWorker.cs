namespace ACS.Simulator.API.Services.Pump;

/// <summary>Vòng poll duy nhất của pump: mỗi tick cách nhau Interval + jitter (tối thiểu 15s), tôn trọng backoff.</summary>
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
            // Delay do service tính: interval + jitter, không ngắn hơn backoff; disabled/auth thì nghỉ dài.
            var delay = pump.GetDelay();
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
