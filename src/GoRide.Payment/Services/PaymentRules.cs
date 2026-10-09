using GoRide.Payment.Checkout;
using GoRide.Payment.Models;
using GoRide.Payment.Verification;

namespace GoRide.Payment.Services;

public static class PaymentRules
{
    public static void ValidateId(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim()
            || value.Any(char.IsControl))
            throw new PaymentException(400, "INVALID_REQUEST", $"{field} must contain 1 to 128 nonblank characters without surrounding whitespace.");
    }

    public static void ValidateCompletion(TripCompletedEvent evt)
    {
        ValidateId(evt.EventId, "eventId");
        ValidateId(evt.TripId, "tripId");
        ValidateId(evt.RiderId, "riderId");
        ValidateId(evt.DriverId, "driverId");
        if (evt.EventType != "TRIP_COMPLETED" || evt.OccurredAt == default)
            throw new PaymentException(400, "INVALID_EVENT", "A TRIP_COMPLETED event with occurredAt is required.");
        ValidateMoney(evt.Payload?.FinalFare ?? evt.Payload?.Fare, "finalFare");
        if (evt.Payload?.EstimatedFare is { } estimate) ValidateMoney(estimate, "estimatedFare");
        if (evt.Payload?.Breakdown is { } b)
        {
            foreach (var part in new[] { b.Base, b.Distance, b.Time, b.Stops, b.Waiting, b.Total })
                ValidateMoney(part, "breakdown");
            if (b.Total != evt.Payload.FinalFare.GetValueOrDefault(evt.Payload.Fare ?? 0)
                || b.Base + b.Distance + b.Time + b.Stops + b.Waiting != b.Total)
                throw new PaymentException(400, "INVALID_FARE", "The breakdown must add up to the final fare.");
        }
    }

    private static void ValidateMoney(decimal? amount, string field)
    {
        if (amount is null or < 0 or > 99999999.99m || decimal.Round(amount.Value, 2) != amount)
            throw new PaymentException(400, "INVALID_FARE", $"{field} must be a nonnegative amount with at most two decimal places.");
    }

    public static PaymentRecord ApplyCompletion(PaymentRecord? current, TripCompletedEvent evt)
    {
        ValidateCompletion(evt);
        var fare = (evt.Payload!.FinalFare ?? evt.Payload.Fare)!.Value;
        if (current is null)
            return new PaymentRecord
            {
                Id = Guid.NewGuid().ToString(),
                TripId = evt.TripId!,
                RiderId = evt.RiderId!,
                DriverId = evt.DriverId!,
                EstimatedFare = evt.Payload.EstimatedFare ?? fare,
                FinalFare = fare,
                Breakdown = evt.Payload.Breakdown,
                CreatedAt = DateTimeOffset.UtcNow,
                FareUpdatedAt = evt.OccurredAt
            };

        if (current.RiderId != evt.RiderId || current.DriverId != evt.DriverId)
            throw new PaymentException(409, "TRIP_IDENTITY_CONFLICT", "The trip already belongs to different participants.");
        // Late redelivery cannot revert a corrected fare or reset a selected method.
        if (evt.OccurredAt < current.FareUpdatedAt) return current;
        if (evt.OccurredAt == current.FareUpdatedAt && fare != current.FinalFare)
            throw new PaymentException(409, "FARE_VERSION_CONFLICT", "Conflicting fares have the same completion time.");
        if (current.Status is "Paid" or "Charged")
        {
            if (fare != current.FinalFare)
                throw new PaymentException(409, "PAYMENT_SETTLED", "A settled payment requires reconciliation before its fare can change.");
            return current;
        }
        return current with
        {
            FinalFare = fare,
            EstimatedFare = evt.Payload.EstimatedFare ?? current.EstimatedFare,
            Breakdown = evt.Payload.Breakdown ?? (fare == current.FinalFare ? current.Breakdown : null),
            FareUpdatedAt = evt.OccurredAt
        };
    }

    // Decides what a signature-verified provider notice does. Only a success notice whose
    // amount and currency match both its order and the current final fare marks the trip
    // paid; anything else is recorded for reconciliation and leaves the payment unchanged.
    public static VerificationDecision ApplyProviderNotice(PaymentRecord payment, CheckoutAttempt attempt, VerificationRecord notice)
    {
        switch (notice.StatusCode)
        {
            case 2:
                var finalMinor = decimal.ToInt64(payment.FinalFare * 100);
                if (notice.TripId != payment.TripId || notice.Currency != attempt.Currency
                    || notice.AmountMinor != attempt.AmountMinor || notice.AmountMinor != finalMinor)
                    return new(payment, VerificationOutcome.AmountMismatch);
                if (payment.Status is "Paid" or "Charged")
                    return new(payment, payment.ProviderPaymentId == notice.PaymentId
                        ? VerificationOutcome.AlreadyPaid : VerificationOutcome.DuplicatePayment);
                return new(payment with
                {
                    Status = "Paid",
                    Method = "Card",
                    ProcessedAt = notice.ReceivedAt,
                    ProviderPaymentId = notice.PaymentId,
                    ProviderOrderId = notice.OrderId
                }, VerificationOutcome.Paid);
            case 0: return new(payment, VerificationOutcome.Pending);
            case -1: return new(payment, VerificationOutcome.Cancelled);
            case -2: return new(payment, VerificationOutcome.Failed);
            case -3: return new(payment, VerificationOutcome.Chargedback);
            default: throw new PaymentException(400, "INVALID_NOTIFICATION", "status_code is not a PayHere status.");
        }
    }

    public static PaymentRecord SelectCard(PaymentRecord payment, string riderId)
    {
        if (payment.RiderId != riderId)
            throw new PaymentException(403, "PAYMENT_FORBIDDEN", "Only the rider who took this trip can select its payment method.");
        if (payment.Status is "Paid" or "Charged")
            throw new PaymentException(409, "PAYMENT_SETTLED", "This trip has already been paid.");
        if (payment.CardDisabled || payment.CardAttemptCount >= 2)
            throw new PaymentException(409, "CARD_DISABLED", "Card payment is disabled for this trip.");
        if (payment.Status != "Pending")
            throw new PaymentException(409, "PAYMENT_NOT_PENDING", "This payment cannot be changed to card.");
        // Selection alone never charges, increments attempts, or marks the trip paid.
        return payment with { Method = "Card" };
    }

    // The rider pays the driver in cash; the trip stays unpaid until the driver confirms it.
    public static PaymentRecord ChooseCash(PaymentRecord payment, string riderId)
    {
        if (payment.RiderId != riderId)
            throw new PaymentException(403, "PAYMENT_FORBIDDEN", "Only the rider who took this trip can choose how to pay.");
        if (payment.Status is "Paid" or "Charged")
            throw new PaymentException(409, "PAYMENT_SETTLED", "This trip has already been paid.");
        if (payment.Status == "AwaitingCash") return payment;
        if (payment.Status != "Pending")
            throw new PaymentException(409, "PAYMENT_NOT_PENDING", "This payment cannot be changed to cash.");
        return payment with { Method = "Cash", Status = "AwaitingCash" };
    }

    // Only the trip's driver can say the cash was received. Repeating it is harmless.
    public static PaymentRecord ConfirmCash(PaymentRecord payment, string driverId, DateTimeOffset at)
    {
        if (payment.DriverId != driverId)
            throw new PaymentException(403, "PAYMENT_FORBIDDEN", "Only this trip's driver can confirm a cash payment.");
        if (payment.Status == "Paid" && payment.Method == "Cash") return payment;
        if (payment.Status != "AwaitingCash")
            throw new PaymentException(409, "CASH_NOT_SELECTED", "The rider has not chosen to pay in cash.");
        return payment with { Status = "Paid", ProcessedAt = at };
    }
}
