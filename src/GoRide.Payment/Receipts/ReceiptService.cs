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

    // Resend always goes to the address captured at checkout; the request cannot name another one.
    public async Task<ReceiptView> ResendAsync(string tripId, string riderId, CancellationToken ct)
    {
        await RequireOwnerAsync(tripId, riderId, ct);
        var row = await RequireReceiptAsync(tripId, ct);
        var now = clock.GetUtcNow();
        if (ResendBlocker(row, now) is { } blocked) throw blocked;
        // A concurrent request got there first.
        if (!await receipts.RequestResendAsync(tripId, now, ct))
            throw new PaymentException(409, "RECEIPT_IN_PROGRESS", "The receipt is already being sent.");
        return View(await RequireReceiptAsync(tripId, ct));
    }

    public ReceiptView View(ReceiptRow row)
    {
        var left = Math.Max(0, ReceiptRules.MaxResends - row.ResendCount);
        return new(row.ReceiptId, row.Status, ReceiptRules.MaskEmail(row.Recipient), row.SentAt, row.Attempts,
            ResendBlocker(row, clock.GetUtcNow()) is null, ResendAvailableAt(row), left);
    }

    // Why a resend is refused right now, or null when it is allowed.
    public static PaymentException? ResendBlocker(ReceiptRow row, DateTimeOffset now)
    {
        if (row.Status == ReceiptStatus.NoEmail || row.Recipient is null)
            return new(409, "RECEIPT_EMAIL_MISSING", "There was no email address on the account when this trip was paid.");
        if (row.Status is not (ReceiptStatus.Sent or ReceiptStatus.Failed))
            return new(409, "RECEIPT_IN_PROGRESS", "The receipt is already being sent.");
        if (row.ResendCount >= ReceiptRules.MaxResends)
            return new(429, "RECEIPT_RESEND_LIMIT", $"The receipt can be resent at most {ReceiptRules.MaxResends} times.");
        if (ResendAvailableAt(row) is { } available && available > now)
            return new(429, "RECEIPT_RESEND_TOO_SOON", "Please wait a minute before asking for the receipt again.")
            {
                RetryAfter = available - now
            };
        return null;
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
        // Validate the request before touching the database.
        PaymentRules.ValidateId(tripId, "tripId");
        var payment = await payments.GetAsync(tripId, ct)
            ?? throw new PaymentException(409, "TRIP_NOT_COMPLETED", "The completed trip and final fare are not available yet.");
        if (payment.RiderId != riderId)
            throw new PaymentException(403, "PAYMENT_FORBIDDEN", "Only this trip's rider can view its receipt.");
    }
}
