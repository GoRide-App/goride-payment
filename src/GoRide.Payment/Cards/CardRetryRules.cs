using GoRide.Payment.Services;

namespace GoRide.Payment.Cards;

public static class CardRetryRules
{
    public static bool IsTransient(string? code) => code?.ToUpperInvariant() is
        "PROCESSING_ERROR" or "PROVIDER_TIMEOUT" or "PROVIDER_UNAVAILABLE";

    public static bool ShouldRetry(string? code, int attempts) => attempts == 1 && IsTransient(code);

    public static string RequireRequestId(string? value) =>
        value?.Length == 36 && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id.ToString("D")
            : throw new PaymentException(400, "INVALID_REQUEST", "requestId must be a nonempty UUID. Reuse it when resending a payment.");

    public static PaymentException Failure(string code, int attempts) => new(402, code, code switch
    {
        "INSUFFICIENT_FUNDS" => "Your card has insufficient funds. Try another card.",
        "EXPIRED_CARD" => "Your card has expired. Try another card.",
        "INCORRECT_CVC" => "Your card's security code is incorrect. Check it or try another card.",
        "CARD_NUMBER_INVALID" => "Your card number is invalid. Try another card.",
        "CARD_DECLINED" => "Your card was declined. Try another card.",
        _ => "Your payment failed after trying once more. Retry or use another card."
    })
    {
        Retryable = IsTransient(code),
        AutoRetried = attempts > 1,
        Attempts = attempts
    };
}
