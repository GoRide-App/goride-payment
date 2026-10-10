using GoRide.Payment.Checkout;
using GoRide.Payment.Cards;
using GoRide.Payment.Confirmation;
using GoRide.Payment.Data;
using GoRide.Payment.Models;

namespace GoRide.Payment.Services;

// What both the rider and the driver see about a trip's payment.
// This status never requires the driver to wait for payment.
public sealed record PaymentStatusView(string TripId, string Status, string? Method, decimal Amount, string Currency,
    DateTimeOffset? PaidAt, string? CardBrand, string? CardLast4, int AttemptCount, string? LastFailureCode,
    string? RequestId, string? RequestState, int RequestAttempts, bool AutoRetried, bool Retryable);

public sealed class PaymentStatusService(PaymentStore payments, ConfirmationStore confirmations,
    TripCompletionService completions, TimeProvider clock, CardAttemptStore attempts)
{
    // Null while the completed trip has not reached the payment service yet.
    public async Task<PaymentStatusView?> GetAsync(string tripId, string userId, CancellationToken ct)
    {
        PaymentRules.ValidateId(tripId, "tripId");
        var payment = await payments.GetAsync(tripId, ct);
        if (payment is null) return null;
        if (userId != payment.RiderId && userId != payment.DriverId)
            throw new PaymentException(403, "PAYMENT_FORBIDDEN", "Only this trip's rider and driver can view its payment.");
        return await ViewAsync(payment, ct);
    }

    // Local demo only: a simulated ride has no trip service record, so the rider app reports
    // its completion. Real trips are only ever completed by the trip service.
    public async Task<PaymentRecord> CompleteDemoTripAsync(string? tripId, decimal? finalFare, string riderId, CancellationToken ct)
    {
        PaymentRules.ValidateId(tripId, "tripId");
        if (!tripId!.StartsWith("trp_", StringComparison.Ordinal) || tripId.Length > 100)
            throw new PaymentException(400, "INVALID_REQUEST", "tripId must be a simulated ride ID.");
        if (finalFare is not (> 0 and <= 100000) || decimal.Round(finalFare.Value, 2) != finalFare.Value)
            throw new PaymentException(400, "INVALID_FARE", "finalFare must be between LKR 0.01 and 100,000 with at most two decimals.");
        if (await payments.GetAsync(tripId, ct) is { } existing) return Owned(existing, riderId);
        var evt = new TripCompletedEvent
        {
            EventId = "demo-" + tripId,
            EventType = "TRIP_COMPLETED",
            TripId = tripId,
            RiderId = riderId,
            DriverId = "demo-driver",
            OccurredAt = clock.GetUtcNow(),
            Payload = new CompletedFare { FinalFare = finalFare, EstimatedFare = finalFare }
        };
        try { return await completions.CompleteAsync(evt, ct); }
        catch (PaymentException ex) when (ex.Code == "EVENT_ID_CONFLICT")
        {
            // A concurrent request created it first.
            var created = await payments.GetAsync(tripId, ct);
            if (created is null) throw;
            return Owned(created, riderId);
        }
    }

    private static PaymentRecord Owned(PaymentRecord payment, string riderId) => payment.RiderId == riderId
        ? payment
        : throw new PaymentException(403, "PAYMENT_FORBIDDEN", "This ride belongs to another rider.");

    private async Task<PaymentStatusView> ViewAsync(PaymentRecord payment, CancellationToken ct)
    {
        var paid = payment.Status is "Paid" or "Charged";
        var confirmation = paid && payment.Method == "Card" ? await confirmations.GetAsync(payment.TripId, ct) : null;
        var history = await attempts.StatusAsync(payment.TripId, ct);
        return new(payment.TripId, paid ? "Paid" : payment.Status, payment.Method, payment.FinalFare, PayHereSettings.Currency,
            paid ? payment.ProcessedAt : null, confirmation?.CardBrand, confirmation?.CardLast4, history.AttemptCount,
            history.LastFailureCode, history.RequestId, history.RequestState, history.RequestAttempts, history.AutoRetried, history.Retryable);
    }
}
