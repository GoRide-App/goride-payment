using System.Security.Cryptography;
using System.Text;
using GoRide.Payment.Services;
using Microsoft.Extensions.Primitives;

namespace GoRide.Payment.Verification;

// The form PayHere posts to notify_url. Values are kept exactly as posted because the
// md5sig is computed over the raw strings.
public sealed record PayHereNotice(
    string MerchantId, string OrderId, string PaymentId, string Amount, string Currency,
    string StatusCode, string Md5Sig, string? Method, string? StatusMessage, string? CardNo)
{
    public static PayHereNotice From(IFormCollection form) => new(
        Required(form, "merchant_id"), Required(form, "order_id"), Required(form, "payment_id"),
        Required(form, "payhere_amount"), Required(form, "payhere_currency"), Required(form, "status_code"),
        Required(form, "md5sig"), Optional(form, "method"), Optional(form, "status_message"), Optional(form, "card_no"));

    // Identifies the delivery: a redelivery with any different detail is a conflict, not a duplicate.
    public string PayloadHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
        MerchantId, OrderId, PaymentId, Amount, Currency, StatusCode, Md5Sig.ToUpperInvariant(), Method, CardNo))));

    private static string Required(IFormCollection form, string name)
    {
        var value = form.TryGetValue(name, out var values) ? values.ToString() : "";
        if (string.IsNullOrWhiteSpace(value))
            throw new PaymentException(400, "INVALID_NOTIFICATION", $"{name} is required.");
        return value;
    }

    private static string? Optional(IFormCollection form, string name) =>
        form.TryGetValue(name, out var values) && !StringValues.IsNullOrEmpty(values) ? values.ToString() : null;
}
