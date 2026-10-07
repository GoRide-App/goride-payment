using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GoRide.Payment.Checkout;

// PayHere's protocol mandates MD5 for both signatures; it is not used for anything else here.
#pragma warning disable CA5351
public static class PayHereSignature
{
    // hash = UPPER(MD5(merchant_id + order_id + amount(0.00) + currency + UPPER(MD5(merchant_secret))))
    public static string CheckoutHash(string merchantId, string orderId, long amountMinor, string currency, string merchantSecret) =>
        Md5Upper(merchantId + orderId + FormatAmount(amountMinor) + currency + Md5Upper(merchantSecret));

    // md5sig = UPPER(MD5(merchant_id + order_id + payhere_amount + payhere_currency + status_code + UPPER(MD5(merchant_secret))))
    // Every value is used exactly as PayHere posted it.
    public static string NotifySignature(string merchantId, string orderId, string payhereAmount, string currency,
        string statusCode, string merchantSecret) =>
        Md5Upper(merchantId + orderId + payhereAmount + currency + statusCode + Md5Upper(merchantSecret));

    public static string FormatAmount(long amountMinor) =>
        (amountMinor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    public static bool Matches(string expected, string? presented) =>
        presented is not null && presented.Length == expected.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(presented.ToUpperInvariant()));

    private static string Md5Upper(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value)));
}
#pragma warning restore CA5351
