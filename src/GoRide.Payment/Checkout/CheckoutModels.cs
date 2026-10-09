namespace GoRide.Payment.Checkout;

public sealed record CheckoutAttempt(
    string TripId, string IdempotencyKey, long AmountMinor, string Currency,
    string SuccessUrl, string CancelUrl, DateTimeOffset CreatedAt,
    string? SessionId = null);

public sealed record HostedSession(string Id, string Status, string PaymentStatus,
    string? Url, long AmountTotal, string Currency, bool LiveMode,
    string ClientReferenceId, long ExpiresAt);

public sealed record CheckoutRedirect(string SessionId, string Url, decimal Amount,
    string Currency, DateTimeOffset ExpiresAt);

public interface ICheckoutProvider
{
    Task<HostedSession> CreateAsync(CheckoutAttempt attempt, CancellationToken ct);
    Task<HostedSession> GetAsync(CheckoutAttempt attempt, CancellationToken ct);
    Task<HostedSession> ExpireAsync(CheckoutAttempt attempt, CancellationToken ct);
}
