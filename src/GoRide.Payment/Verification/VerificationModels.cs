using GoRide.Payment.Models;

namespace GoRide.Payment.Verification;

// One provider notice for one payment and status. PayHere may post the same notice
// several times; (Provider, PaymentId, StatusCode) identifies it so it is stored once.
public sealed record VerificationRecord(
    string Provider, string PaymentId, int StatusCode, string OrderId, string TripId,
    long AmountMinor, string Currency, string PayloadHash, string? PaymentMethod, string? CardMasked,
    DateTimeOffset ReceivedAt);

// What a verified notice did to the payment. Outcome is stored with the notice.
public sealed record VerificationDecision(PaymentRecord Payment, string Outcome);

public sealed record VerificationResult(string Outcome, PaymentRecord Payment, bool Duplicate);

public static class VerificationOutcome
{
    public const string Paid = "Paid";
    public const string AlreadyPaid = "AlreadyPaid";
    public const string Pending = "Pending";
    public const string Cancelled = "Cancelled";
    public const string Failed = "Failed";
    public const string Chargedback = "Chargedback";
    public const string AmountMismatch = "AmountMismatch";
    public const string DuplicatePayment = "DuplicatePayment";
}
