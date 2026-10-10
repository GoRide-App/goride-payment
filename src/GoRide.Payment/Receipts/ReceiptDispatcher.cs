namespace GoRide.Payment.Receipts;

// SCRUM-105: delivers receipts from the payment_receipts outbox. Each row is claimed with a
// lease before sending, retried with backoff on transient failures, and failed after
// MaxDeliveryAttempts (or at once when the provider rejects it permanently).
public sealed class ReceiptDispatcher(IServiceScopeFactory scopes, IConfiguration configuration,
    TimeProvider clock, ILogger<ReceiptDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Receipts:DispatcherEnabled", true)) return;
        var poll = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Receipts:PollSeconds", 5), 1, 300));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { while (await SendNextAsync(stoppingToken)) { } }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Receipt dispatch failed; retrying shortly."); }
            try { await Task.Delay(poll, clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Returns true when a receipt was processed, so a backlog drains without waiting.
    public async Task<bool> SendNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ReceiptStore>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var claimed = await store.ClaimNextAsync(clock.GetUtcNow(), ct);
        if (claimed is null) return false;
        var (content, attempts, token) = claimed.Value;
        try
        {
            var messageId = await sender.SendAsync(ReceiptRenderer.Render(content), ct);
            await store.MarkSentAsync(content.TripId, token, sender.Name, messageId, clock.GetUtcNow(), ct);
            logger.LogInformation("Receipt {ReceiptId} processed via {Provider}.", content.ReceiptId, sender.Name);
        }
        catch (EmailDeliveryException ex)
        {
            var final = ex.Permanent || attempts >= ReceiptRules.MaxDeliveryAttempts;
            var now = clock.GetUtcNow();
            await store.MarkFailedAsync(content.TripId, token, final ? ReceiptStatus.Failed : ReceiptStatus.Retry,
                final ? now : now + ReceiptRules.RetryDelay(attempts), ex.Message, ct);
            logger.LogWarning("Receipt {ReceiptId} attempt {Attempt} failed: {Error}", content.ReceiptId, attempts, ex.Message);
        }
        return true;
    }
}
