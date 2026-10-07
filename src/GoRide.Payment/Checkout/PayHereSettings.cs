using GoRide.Payment.Services;

namespace GoRide.Payment.Checkout;

// PayHere sandbox only. The checkout address is fixed so a misconfigured server can
// never send riders to the live gateway; no live merchant credentials are needed.
public sealed class PayHereSettings(IConfiguration configuration, IWebHostEnvironment environment)
{
    public const string CheckoutUrl = "https://sandbox.payhere.lk/pay/checkout";
    public const string Currency = "LKR";

    public string MerchantId => configuration["PayHere:MerchantId"] ?? "";
    public string MerchantSecret => configuration["PayHere:MerchantSecret"] ?? "";

    public void Validate()
    {
        if (MerchantId.Length is < 4 or > 20 || !MerchantId.All(char.IsAsciiDigit)
            || MerchantSecret.Length is < 8 or > 256 || MerchantSecret.Any(c => !char.IsAscii(c) || char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new PaymentException(503, "PAYHERE_NOT_CONFIGURED", "Configure a PayHere sandbox merchant ID and secret on the server.");
        ValidateUrl(configuration["PayHere:ReturnUrl"]);
        ValidateUrl(configuration["PayHere:CancelUrl"]);
        ValidateUrl(configuration["PayHere:NotifyUrl"]);
    }

    public (string Return, string Cancel, string Notify) Urls(string tripId)
    {
        Validate();
        return (AddTrip(configuration["PayHere:ReturnUrl"]!, tripId), AddTrip(configuration["PayHere:CancelUrl"]!, tripId),
            configuration["PayHere:NotifyUrl"]!);
    }

    private void ValidateUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != "https" && !(environment.IsDevelopment() && uri.Scheme == "http" && uri.IsLoopback)))
            throw new PaymentException(503, "CHECKOUT_NOT_CONFIGURED", "Configure HTTPS PayHere return, cancel and notify URLs (HTTP localhost is permitted in Development).");
    }

    private static string AddTrip(string url, string tripId) =>
        url + (url.Contains('?') ? "&" : "?") + "tripId=" + Uri.EscapeDataString(tripId);
}
