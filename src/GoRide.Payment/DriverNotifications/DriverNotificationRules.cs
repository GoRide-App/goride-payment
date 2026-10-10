using System.Globalization;
using GoRide.Payment.Checkout;
using GoRide.Payment.Models;
using GoRide.Payment.Services;
using GoRide.Payment.Verification;

namespace GoRide.Payment.DriverNotifications;

public sealed record DriverNotification(string TripId, decimal Amount, string Currency,
    string? CardBrand, string? CardLast4, DateTimeOffset PaidAt);

public sealed record DriverNotificationContent(string EventId, string PaymentId, string DriverId, DriverNotification Notification);
public sealed record DriverNotificationPage(IReadOnlyList<DriverNotification> Notifications, long? NextCursor);

public static class DriverNotificationRules
{
    public const int PageSize = 100;
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public static DriverNotificationContent? ForPaid(PaymentRecord payment, VerificationRecord notice)
    {
        if (payment.Status != "Paid" || payment.Method != "Card") return null;
        var card = notice.CardMasked;
        var last4 = card is { Length: >= 4 } && card[^4..].All(char.IsAsciiDigit) ? card[^4..] : null;
        return new(Guid.NewGuid().ToString(), payment.Id, payment.DriverId,
            new(payment.TripId, payment.FinalFare, PayHereSettings.Currency, notice.PaymentMethod, last4,
                payment.ProcessedAt ?? throw new InvalidOperationException("A paid payment must have a processing time.")));
    }

    // Require an ISO timestamp with a timezone; never interpret a browser's local time as UTC.
    public static DateTimeOffset Since(string? value, DateTimeOffset now)
    {
        if (value is null) return now - TimeSpan.FromDays(7);
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        if (!DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var since) || since > now)
            throw new PaymentException(400, "INVALID_SINCE", "since must be an ISO 8601 timestamp with a timezone, no later than now.");
        return since;
    }

    public static long After(string? value)
    {
        if (value is null) return 0;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var after) || after < 0)
            throw new PaymentException(400, "INVALID_CURSOR", "after must be a non-negative integer cursor.");
        return after;
    }

    public static TimeSpan RetryDelay(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(30)
    };
}
