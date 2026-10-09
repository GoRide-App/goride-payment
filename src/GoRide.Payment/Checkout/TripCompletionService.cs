using GoRide.Payment.Data;
using GoRide.Payment.Models;
using GoRide.Payment.Services;

namespace GoRide.Payment.Checkout;

public sealed class TripCompletionService(PaymentStore payments, CheckoutStore checkouts)
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
        return await payments.CompleteAsync(evt, ct);
    }
}
