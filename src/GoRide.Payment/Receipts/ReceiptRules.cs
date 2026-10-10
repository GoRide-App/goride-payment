using System.Net.Mail;

namespace GoRide.Payment.Receipts;

public static class ReceiptStatus
{
    public const string Pending = "Pending";
    public const string Sending = "Sending";
    public const string Retry = "Retry";
    public const string Sent = "Sent";
    public const string Logged = "Logged";
    public const string Failed = "Failed";
    public const string NoEmail = "NoEmail";
}

// SCRUM-105 receipt rules, kept pure so they are easy to unit test.
public static class ReceiptRules
{
    public const int MaxDeliveryAttempts = 5;
    public const int MaxResends = 3;
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public static bool IsDeliverableEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || value != value.Trim()
            || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return false;
        try
        {
            var address = new MailAddress(value);
            var host = address.Host;
            return address.Address == value && host.Contains('.') && !host.StartsWith('.') && !host.EndsWith('.');
        }
        catch (FormatException) { return false; }
    }

    // s***a@gmail.com: enough for the rider to recognise the address without exposing it.
    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email)) return null;
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        var local = email[..at];
        var masked = local.Length <= 2 ? local[0] + "***" : local[0] + "***" + local[^1];
        return masked + email[at..];
    }

    // Delay before the next delivery attempt after a failed one: 30s, 2m, 10m, 30m.
    public static TimeSpan RetryDelay(int attemptsSoFar) => attemptsSoFar switch
    {
        <= 1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(30)
    };

    public static string ErrorSummary(string message) =>
        message.Length <= 300 ? message : message[..297] + "...";
}
