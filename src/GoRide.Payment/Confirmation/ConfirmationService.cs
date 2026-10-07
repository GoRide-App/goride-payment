using GoRide.Payment.Data;
using GoRide.Payment.Services;
using GoRide.Payment.Verification;

namespace GoRide.Payment.Confirmation;

// SCRUM-104: the in-app confirmation exists only after SCRUM-103 verified the card payment.
// PayHere passes no status to the return URL, so the app asks here after returning.
public sealed class ConfirmationService(PaymentStore payments, ConfirmationStore confirmations,
    VerificationStore verifications, TimeProvider clock)
{
    public async Task<ConfirmationView> GetAsync(string tripId, string riderId, CancellationToken ct)
    {
        await RequireOwnerAsync(tripId, riderId, ct);
        var confirmation = await confirmations.GetAsync(tripId, ct);
        if (confirmation is not null) return new(ConfirmationStatus.Confirmed, confirmation, null);
        var outcomes = await verifications.OutcomesAsync(tripId, ct);
        return new(ConfirmationStatus.Pending, null, outcomes.Count == 0 ? null : outcomes[^1].Outcome);
    }

    public async Task<PaymentConfirmation> AcknowledgeAsync(string tripId, string riderId, string confirmationId, CancellationToken ct)
    {
        await RequireOwnerAsync(tripId, riderId, ct);
        return await confirmations.AcknowledgeAsync(tripId, confirmationId, clock.GetUtcNow(), ct)
            ?? throw new PaymentException(409, "PAYMENT_NOT_CONFIRMED", "There is no confirmation to acknowledge for this trip yet.");
    }

    private async Task RequireOwnerAsync(string tripId, string riderId, CancellationToken ct)
    {
        var payment = await payments.GetAsync(tripId, ct)
            ?? throw new PaymentException(409, "TRIP_NOT_COMPLETED", "The completed trip and final fare are not available yet.");
        if (payment.RiderId != riderId)
            throw new PaymentException(403, "PAYMENT_FORBIDDEN", "Only this trip's rider can view its payment confirmation.");
    }
}
