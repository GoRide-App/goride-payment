using System.Globalization;
using GoRide.Payment.Checkout;
using GoRide.Payment.Services;

namespace GoRide.Payment.Verification;

// SCRUM-103: a card payment is marked paid only after PayHere's notice is verified.
// Nothing in the notice is trusted until its merchant and signature check out; the order
// then fixes the trip and the amount, so a notice cannot pay for anything else.
public sealed class PaymentVerificationService(VerificationStore store, CheckoutStore checkouts,
    PayHereSettings settings, TimeProvider clock, ILogger<PaymentVerificationService> logger)
{
    public const string Provider = "PayHere";

    public async Task<VerificationResult> VerifyAsync(PayHereNotice notice, CancellationToken ct)
    {
        settings.Validate();
        if (notice.MerchantId != settings.MerchantId)
            throw new PaymentException(401, "UNKNOWN_MERCHANT", "The notification is not for this merchant.");
        var expected = PayHereSignature.NotifySignature(notice.MerchantId, notice.OrderId, notice.Amount,
            notice.Currency, notice.StatusCode, settings.MerchantSecret);
        if (!PayHereSignature.Matches(expected, notice.Md5Sig))
        {
            logger.LogWarning("Rejected a PayHere notification with an invalid signature for order {OrderId}.", notice.OrderId);
            throw new PaymentException(401, "INVALID_SIGNATURE", "The notification signature could not be verified.");
        }

        var attempt = await store.FindAttemptAsync(notice.OrderId, ct)
            ?? throw new PaymentException(404, "ORDER_NOT_FOUND", "No checkout exists for this order.");
        await using var tripLock = await checkouts.LockAsync(attempt.TripId, ct);
        var record = new VerificationRecord(Provider, notice.PaymentId, int.Parse(notice.StatusCode, CultureInfo.InvariantCulture),
            notice.OrderId, attempt.TripId, ToMinor(notice.Amount), notice.Currency, notice.PayloadHash(),
            notice.Method, notice.CardNo, clock.GetUtcNow());
        var result = await store.RecordAsync(record, payment => PaymentRules.ApplyProviderNotice(payment, attempt, record), ct);

        if (result.Outcome is VerificationOutcome.AmountMismatch or VerificationOutcome.DuplicatePayment or VerificationOutcome.Chargedback)
            logger.LogWarning("PayHere payment {PaymentId} for trip {TripId} needs reconciliation: {Outcome}.",
                notice.PaymentId, attempt.TripId, result.Outcome);
        return result;
    }

    private static long ToMinor(string amount) =>
        decimal.ToInt64(decimal.Parse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) * 100);
}
