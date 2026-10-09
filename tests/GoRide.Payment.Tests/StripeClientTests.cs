using GoRide.Payment.Checkout;
using GoRide.Payment.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace GoRide.Payment.Tests;

public sealed class StripeClientTests
{
    private static readonly CheckoutAttempt Attempt = new("trip-1", "key-1", 72550, "lkr", "https://app.test/return", "https://app.test/cancel", DateTimeOffset.UtcNow);
    private static readonly HostedSession Session = new("cs_test_example", "open", "unpaid", "https://checkout.stripe.com/c/pay/cs_test_example", 72550, "lkr", false, "trip-1", DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

    [Theory]
    [InlineData("http://checkout.stripe.com/pay/test")]
    [InlineData("https://checkout.stripe.com.evil.test/pay")]
    [InlineData("https://checkout.stripe.com@evil.test/pay")]
    [InlineData("https://user@checkout.stripe.com/pay")]
    [InlineData("https://checkout.stripe.com:444/pay")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//checkout.stripe.com/pay")]
    [InlineData("https://evil.test/pay")]
    [InlineData("https://checkout.stripe.com/pay\r\nX-Evil:true")]
    [InlineData(null)]
    public void UnsafeRedirectIsRejected(string? url) => Assert.False(StripeCheckoutClient.IsSafeCheckoutUrl(url));

    [Fact]
    public void StripeHttpsUrlWithSessionFragmentIsAccepted() => Assert.True(StripeCheckoutClient.IsSafeCheckoutUrl(Session.Url + "#client-data"));

    [Fact]
    public void MismatchedOrLiveProviderResponseIsRejected()
    {
        foreach (var session in new[]
        {
            Session with { LiveMode = true }, Session with { AmountTotal = 1 }, Session with { Currency = "usd" },
            Session with { ClientReferenceId = "other-trip" }, Session with { Id = "cs_live_example" },
            Session with { Id = "cs_test_example/../../other" }, Session with { Status = "unknown" },
            Session with { PaymentStatus = "unknown" }, Session with { ExpiresAt = long.MaxValue },
            Session with { Url = null }
        }) Assert.Equal("INVALID_CHECKOUT_RESPONSE", Assert.Throws<PaymentException>(() => StripeCheckoutClient.ValidateSession(session, Attempt)).Code);
        Assert.Throws<PaymentException>(() => StripeCheckoutClient.ValidateSession(Session, Attempt with { SessionId = "cs_test_another" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sk_live_not_allowed")]
    [InlineData("pk_test_not_a_secret")]
    [InlineData("sk_test_")]
    [InlineData("sk_test_bad\r\nheader")]
    [InlineData("sk_test_bad_é")]
    public void LivePublishableOrMissingKeysAreRejected(string key)
    {
        var settings = Settings(key, "https://app.test/return", "Production");
        Assert.Equal("STRIPE_TEST_KEY_REQUIRED", Assert.Throws<PaymentException>(settings.Validate).Code);
    }

    [Theory]
    [InlineData("http://app.test/return", "Development")]
    [InlineData("http://localhost:8083/dev/payments", "Production")]
    [InlineData("https://user:password@app.test/return", "Development")]
    [InlineData("https://app.test/return#fragment", "Development")]
    [InlineData("//evil.test/return", "Development")]
    [InlineData("javascript:alert(1)", "Development")]
    public void InsecureConfiguredReturnUrlIsRejected(string url, string environment) =>
        Assert.Equal("CHECKOUT_NOT_CONFIGURED", Assert.Throws<PaymentException>(() => Settings("sk_test_automated_placeholder", url, environment).Validate()).Code);

    [Fact]
    public void LocalHttpReturnIsAllowedOnlyInDevelopmentAndTripIdIsEncoded()
    {
        var settings = Settings("sk_test_automated_placeholder", "http://localhost:8083/dev/payments?result=return", "Development");
        var urls = settings.ReturnUrls("trip?injected=true&more=yes");
        Assert.EndsWith("&tripId=trip%3Finjected%3Dtrue%26more%3Dyes", urls.Success);
    }

    private static StripeSettings Settings(string key, string url, string environment) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Stripe:SecretKey"] = key, ["Stripe:SuccessUrl"] = url, ["Stripe:CancelUrl"] = url }).Build(),
        new TestEnvironment { EnvironmentName = environment });
}

internal sealed class TestEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "GoRide.Payment";
    public string ContentRootPath { get; set; } = "";
    public string WebRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
