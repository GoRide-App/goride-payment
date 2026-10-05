using GoRide.Payment.Data;
using GoRide.Payment.Models;
using GoRide.Payment.Services;

namespace GoRide.Payment.Checkout;

public sealed class TripCompletionService(PaymentStore payments, CheckoutStore checkouts, CheckoutService checkout)
{
    public async Task<PaymentRecord> CompleteAsync(TripCompletedEvent evt, CancellationToken ct)
    {
        PaymentRules.ValidateCompletion(evt);
        await using var tripLock = await checkouts.LockAsync(evt.TripId!, ct);
        // Check inbox reuse before any provider side effect, including expiration.
        var duplicate = await payments.FindProcessedEventAsync(evt, ct);
        if (duplicate is not null) return duplicate;
        var current = await payments.GetAsync(evt.TripId!, ct);
        var updated = PaymentRules.ApplyCompletion(current, evt);
        if (current is not null && updated.FinalFare != current.FinalFare)
            await checkout.ExpireForFareChangeAsync(evt.TripId!, ct);
        return await payments.CompleteAsync(evt, ct);
    }
}
