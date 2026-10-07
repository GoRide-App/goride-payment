namespace GoRide.Payment.Confirmation;

// What the rider app shows after a verified card payment (SCRUM-104).
public sealed record PaymentConfirmation(
    string ConfirmationId, string TripId, decimal Amount, string Currency, string Method,
    string? CardBrand, string? CardLast4, string ProviderReference, DateTimeOffset PaidAt,
    DateTimeOffset? AcknowledgedAt);

// Status is "Confirmed" once the payment is verified, otherwise "Pending". While pending,
// LastProviderOutcome tells the app whether PayHere reported a failure or cancellation.
public sealed record ConfirmationView(string Status, PaymentConfirmation? Confirmation, string? LastProviderOutcome);

public static class ConfirmationStatus
{
    public const string Confirmed = "Confirmed";
    public const string Pending = "Pending";
}
