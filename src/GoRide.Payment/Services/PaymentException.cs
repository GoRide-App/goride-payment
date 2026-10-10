namespace GoRide.Payment.Services;

public sealed class PaymentException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public bool? Retryable { get; init; }
    public bool? AutoRetried { get; init; }
    public int? Attempts { get; init; }

    // Sent as Retry-After when the caller only has to wait (429).
    public TimeSpan? RetryAfter { get; init; }
}
