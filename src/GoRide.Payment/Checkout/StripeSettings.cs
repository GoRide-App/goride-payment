using GoRide.Payment.Services;

namespace GoRide.Payment.Checkout;

public sealed class StripeSettings(IConfiguration configuration, IWebHostEnvironment environment)
{
    public string SecretKey => configuration["Stripe:SecretKey"] ?? "";

    public void Validate()
    {
        // This integration is deliberately test-only, including outside Development.
        if (!SecretKey.StartsWith("sk_test_", StringComparison.Ordinal) || SecretKey.Length <= 12
            || SecretKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new PaymentException(503, "STRIPE_TEST_KEY_REQUIRED", "Configure a Stripe test secret key on the server. Live keys are not accepted.");
        ValidateReturnUrl(configuration["Stripe:SuccessUrl"]);
        ValidateReturnUrl(configuration["Stripe:CancelUrl"]);
    }

    public (string Success, string Cancel) ReturnUrls(string tripId)
    {
        Validate();
        return (AddTrip(configuration["Stripe:SuccessUrl"]!, tripId), AddTrip(configuration["Stripe:CancelUrl"]!, tripId));
    }

    private void ValidateReturnUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != "https" && !(environment.IsDevelopment() && uri.Scheme == "http" && uri.IsLoopback)))
            throw new PaymentException(503, "CHECKOUT_NOT_CONFIGURED", "Configure HTTPS checkout return URLs (HTTP localhost is permitted in Development).");
    }

    private static string AddTrip(string url, string tripId) =>
        url + (url.Contains('?') ? "&" : "?") + "tripId=" + Uri.EscapeDataString(tripId);
}
