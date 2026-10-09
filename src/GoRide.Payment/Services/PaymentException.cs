namespace GoRide.Payment.Services;

public sealed class PaymentException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    // Sent as Retry-After when the caller only has to wait (429).
    public TimeSpan? RetryAfter { get; init; }
}
