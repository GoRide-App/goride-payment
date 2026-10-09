using GoRide.Payment.Data;
using GoRide.Payment.Services;

namespace GoRide.Payment.Receipts;

public sealed record ReceiptRow(string TripId, string ReceiptId, string? Recipient, string Status, int Attempts,
    int ResendCount, DateTimeOffset? SentAt, DateTimeOffset? LastRequestedAt, string? Provider, string? LastError);

// What the rider app shows about the email receipt. The address is masked.
public sealed record ReceiptView(string ReceiptId, string Status, string? Recipient, DateTimeOffset? SentAt,
    int Attempts, bool CanResend, DateTimeOffset? ResendAvailableAt, int ResendsLeft);

// SCRUM-105: the rider can see whether their receipt email was sent and ask for it again.
public sealed class ReceiptService(PaymentStore payments, ReceiptStore receipts, TimeProvider clock)
{
    public async Task<ReceiptView> GetAsync(string tripId, string riderId, CancellationToken ct)
    {
        await RequireOwnerAsync(tripId, riderId, ct);
        return View(await RequireReceiptAsync(tripId, ct));
    }

    public async Task<ReceiptView> ResendAsync(string tripId, string riderId, CancellationToken ct)
    {
        await RequireOwnerAsync(tripId, riderId, ct);
        await RequireReceiptAsync(tripId, ct);
        await receipts.RequestResendAsync(tripId, clock.GetUtcNow(), ct);
        return View(await RequireReceiptAsync(tripId, ct));
    }

    public ReceiptView View(ReceiptRow row)
    {
        var finished = row.Status is ReceiptStatus.Sent or ReceiptStatus.Failed;
        var available = ResendAvailableAt(row);
        var left = Math.Max(0, ReceiptRules.MaxResends - row.ResendCount);
        var canResend = finished && row.Recipient is not null && left > 0 && (available is null || available <= clock.GetUtcNow());
        return new(row.ReceiptId, row.Status, ReceiptRules.MaskEmail(row.Recipient), row.SentAt, row.Attempts,
            canResend, available, left);
    }

    // The cooldown runs from the latest send or resend request, whichever is later.
    public static DateTimeOffset? ResendAvailableAt(ReceiptRow row)
    {
        DateTimeOffset? last = (row.SentAt, row.LastRequestedAt) switch
        {
            ({ } sent, { } requested) => sent > requested ? sent : requested,
            (var sent, var requested) => sent ?? requested
        };
        return last + ReceiptRules.ResendCooldown;
    }

    private async Task<ReceiptRow> RequireReceiptAsync(string tripId, CancellationToken ct) =>
        await receipts.GetAsync(tripId, ct)
            ?? throw new PaymentException(409, "RECEIPT_NOT_AVAILABLE", "The receipt is sent once the card payment is confirmed.");

    private async Task RequireOwnerAsync(string tripId, string riderId, CancellationToken ct)
    {
        var payment = await payments.GetAsync(tripId, ct)
            ?? throw new PaymentException(409, "TRIP_NOT_COMPLETED", "The completed trip and final fare are not available yet.");
        if (payment.RiderId != riderId)
            throw new PaymentException(403, "PAYMENT_FORBIDDEN", "Only this trip's rider can view its receipt.");
    }
}
