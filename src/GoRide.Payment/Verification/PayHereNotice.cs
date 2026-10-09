using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GoRide.Payment.Services;

namespace GoRide.Payment.Verification;

// The form PayHere posts to notify_url. Values are kept exactly as posted because the
// md5sig is computed over the raw strings. Validation rejects anything PayHere would
// never send before any signature or database work happens; messages name the field
// but never echo the submitted value.
public sealed partial record PayHereNotice(
    string MerchantId, string OrderId, string PaymentId, string Amount, string Currency,
    string StatusCode, string Md5Sig, string? Method, string? StatusMessage, string? CardNo)
{
    public const int MaxFields = 40;
    private static readonly string[] Currencies = ["LKR", "USD", "GBP", "EUR", "AUD"];
    private static readonly string[] Statuses = ["2", "0", "-1", "-2", "-3"];

    public static PayHereNotice From(IFormCollection form)
    {
        if (form.Count > MaxFields)
            throw Invalid("The notification has too many fields.");
        var notice = new PayHereNotice(
            Required(form, "merchant_id", MerchantPattern()),
            Required(form, "order_id", OrderPattern()),
            Required(form, "payment_id", PaymentPattern()),
            Required(form, "payhere_amount", AmountPattern()),
            Required(form, "payhere_currency", null),
            Required(form, "status_code", null),
            Required(form, "md5sig", SignaturePattern()),
            Optional(form, "method", MethodPattern()),
            Optional(form, "status_message", null),
            Optional(form, "card_no", CardPattern()));
        if (!Currencies.Contains(notice.Currency, StringComparer.Ordinal))
            throw Invalid("payhere_currency is not a PayHere currency.");
        if (!Statuses.Contains(notice.StatusCode, StringComparer.Ordinal))
            throw Invalid("status_code is not a PayHere status.");
        return notice;
    }

    // Identifies the delivery: a redelivery with any different detail is a conflict, not a duplicate.
    public string PayloadHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
        MerchantId, OrderId, PaymentId, Amount, Currency, StatusCode, Md5Sig.ToUpperInvariant(), Method, CardNo))));

    private static string Required(IFormCollection form, string name, Regex? pattern) =>
        Optional(form, name, pattern) ?? throw Invalid($"{name} is required.");

    private static string? Optional(IFormCollection form, string name, Regex? pattern)
    {
        if (!form.TryGetValue(name, out var values) || values.Count == 0) return null;
        if (values.Count > 1) throw Invalid($"{name} must appear once.");
        var value = values[0] ?? "";
        if (value.Length == 0) return null;
        if (value.Length > 256 || value.Any(char.IsControl) || value != value.Trim() || (pattern is not null && !pattern.IsMatch(value)))
            throw Invalid($"{name} is not in the format PayHere sends.");
        return value;
    }

    private static PaymentException Invalid(string message) => new(400, "INVALID_NOTIFICATION", message);

    [GeneratedRegex("^[0-9]{4,20}$")] private static partial Regex MerchantPattern();
    [GeneratedRegex("^goride-[0-9a-f]{32}$")] private static partial Regex OrderPattern();
    [GeneratedRegex("^[0-9]{1,20}$")] private static partial Regex PaymentPattern();
    [GeneratedRegex("^[0-9]{1,6}\\.[0-9]{2}$")] private static partial Regex AmountPattern();
    [GeneratedRegex("^[0-9A-Fa-f]{32}$")] private static partial Regex SignaturePattern();
    [GeneratedRegex("^[A-Za-z_]{1,16}$")] private static partial Regex MethodPattern();
    [GeneratedRegex("^[0-9*Xx ]{4,32}$")] private static partial Regex CardPattern();
}
