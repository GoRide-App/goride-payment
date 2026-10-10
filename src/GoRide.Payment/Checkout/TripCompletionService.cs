using GoRide.Payment.Data;
using GoRide.Payment.Cards;
using GoRide.Payment.Models;
using GoRide.Payment.Services;

namespace GoRide.Payment.Checkout;

public sealed class TripCompletionService(PaymentStore payments, CheckoutStore checkouts, CardAttemptStore attempts)
{
    public async Task<PaymentRecord> CompleteAsync(TripCompletedEvent evt, CancellationToken ct)
    {
        PaymentRules.ValidateCompletion(evt);
        // The trip lock serializes fare changes with checkout creation and provider
        // verification. A PayHere order has nothing to expire at the provider: a payment
        // made against a superseded amount is rejected when it is verified.
        await using var tripLock = await checkouts.LockAsync(evt.TripId!, ct);
        var duplicate = await payments.FindProcessedEventAsync(evt, ct);
        if (duplicate is not null) return duplicate;
        // A crashed in-app request must resolve its persisted amount before a correction.
        var request = await attempts.GetAsync(evt.TripId!, null, ct);
        if (request?.State is "Processing" or "Retrying"
            && await payments.GetAsync(evt.TripId!, ct) is { } current
            && evt.OccurredAt >= current.FareUpdatedAt
            && (evt.Payload!.FinalFare ?? evt.Payload.Fare) != current.FinalFare)
            throw new PaymentException(409, "PAYMENT_IN_PROGRESS", "Finish the card payment before correcting its fare.");
        return await payments.CompleteAsync(evt, ct);
    }
}
