namespace GoRide.Payment.DriverNotifications;

public sealed class DriverNotificationDispatcher(IServiceScopeFactory scopes, IConfiguration configuration,
    TimeProvider clock, ILogger<DriverNotificationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Notification:DispatcherEnabled", true)) return;
        var poll = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Notification:PollSeconds", 5), 1, 300));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { while (await SendNextAsync(stoppingToken)) { } }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Driver notification dispatch failed; retrying shortly."); }
            try { await Task.Delay(poll, clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<bool> SendNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DriverNotificationStore>();
        var sender = scope.ServiceProvider.GetRequiredService<IDriverNotificationSender>();
        var claimed = await store.ClaimNextAsync(clock.GetUtcNow(), ct);
        if (claimed is null) return false;
        var (content, attempts, token) = claimed.Value;
        var now = clock.GetUtcNow();
        try
        {
            // Expired leases count as attempts too; a repeatedly crashing worker is bounded.
            if (attempts > DriverNotificationRules.MaxAttempts)
                throw new DriverNotificationDeliveryException("NOTIFICATION_ATTEMPTS_EXHAUSTED", permanent: true);
            var status = await sender.SendAsync(content, ct);
            await store.FinishAsync(content.Notification.TripId, token, status, clock.GetUtcNow(), now, null, ct);
        }
        catch (DriverNotificationDeliveryException ex)
        {
            var final = ex.Permanent || attempts >= DriverNotificationRules.MaxAttempts;
            now = clock.GetUtcNow();
            await store.FinishAsync(content.Notification.TripId, token, final ? "Failed" : "Retry", now,
                final ? now : now + DriverNotificationRules.RetryDelay(attempts), ex.Message, ct);
            logger.LogWarning("Driver notification {EventId} attempt {Attempt} failed: {Code}", content.EventId, attempts, ex.Message);
        }
        return true;
    }
}
