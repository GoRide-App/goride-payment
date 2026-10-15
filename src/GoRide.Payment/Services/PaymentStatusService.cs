using System.Text.RegularExpressions;
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

public sealed partial class PaymentStatusService(PaymentStore payments, ConfirmationStore confirmations,
    CheckoutStore checkouts, TimeProvider clock, CardAttemptStore attempts, IConfiguration configuration)
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

    // A simulated ride has no trip service record, so the rider app reports
    // its completion. Real trips are only ever completed by the trip service.
    public async Task<PaymentRecord> CompleteDemoTripAsync(string? tripId, decimal? finalFare, string riderId, CancellationToken ct)
    {
        PaymentRules.ValidateId(tripId, "tripId");
        if (!DemoTripPattern().IsMatch(tripId!))
            throw new PaymentException(400, "INVALID_REQUEST", "tripId must match demo_trp_ followed by 1 to 100 letters, digits, underscores or hyphens.");
        var maxFare = configuration.GetValue<decimal?>("DemoTrips:MaxFare") ?? 100000m;
        if (finalFare is not > 0 || finalFare > maxFare || decimal.Round(finalFare.Value, 2) != finalFare.Value)
            throw new PaymentException(400, "INVALID_FARE", $"finalFare must be positive, at most {maxFare} LKR, with at most two decimals.");
        // Share the checkout lock across instances, including concurrent first completions.
        // Demo fares are immutable: a repeat must never correct or reassign a payment.
        await using var tripLock = await checkouts.LockAsync(tripId!, ct);
        if (await payments.GetAsync(tripId!, ct) is { } existing)
        {
            if (existing.RiderId != riderId || existing.FinalFare != finalFare)
                throw new PaymentException(409, "DEMO_TRIP_CONFLICT", "This demo trip already has a different rider or final fare.");
            return existing;
        }
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
        return await payments.CompleteAsync(evt, ct);
    }

    [GeneratedRegex("^demo_trp_[A-Za-z0-9_-]{1,100}$")]
    private static partial Regex DemoTripPattern();

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
