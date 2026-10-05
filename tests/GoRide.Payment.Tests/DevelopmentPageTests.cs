using System.Net;
using System.Net.Http.Json;
using GoRide.Payment.Checkout;
using GoRide.Payment.Models;
using Xunit;

namespace GoRide.Payment.Tests;

public sealed class DevelopmentPageTests
{
    private static Dictionary<string, string?> Enabled => new() { ["DevelopmentCheckout:Enabled"] = "true" };

    [Fact]
    public async Task DevelopmentPageRequiresDevelopmentEnvironmentAndExplicitEnablement()
    {
        foreach (var environment in new[] { "Production", "Staging", "Testing" })
        {
            await using var app = new PaymentApplication("unused", environment: environment, settings: Enabled);
            using var client = app.CreateClient();
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/payments")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/payments/app.js")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/dev/payments/trips", new { finalFare = 725.50 })).StatusCode);
        }
        await using var disabled = new PaymentApplication("unused", environment: "Development");
        using var disabledClient = disabled.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await disabledClient.GetAsync("/dev/payments")).StatusCode);
    }

    [Fact]
    public async Task DevelopmentAssetsAreLocalOnlyAndContainNoSecret()
    {
        await using var app = new PaymentApplication("unused", environment: "Development", settings: Enabled);
        using var client = app.CreateClient();
        foreach (var path in new[] { "", "/app.js", "/app.css", "/config" })
        {
            var response = await client.GetAsync("/dev/payments" + path);
            response.EnsureSuccessStatusCode();
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.DoesNotContain("sk_test_automated_placeholder", await response.Content.ReadAsStringAsync());
        }
        using var remoteHost = new HttpRequestMessage(HttpMethod.Get, "/dev/payments");
        remoteHost.Headers.Host = "external.example";
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(remoteHost)).StatusCode);
        await using var remote = new PaymentApplication("unused", environment: "Development", settings: Enabled, remoteIp: IPAddress.Parse("192.0.2.5"));
        using var remoteClient = remote.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await remoteClient.GetAsync("/dev/payments")).StatusCode);
    }

    [Fact]
    public async Task CrossOriginAndHeaderlessDevelopmentMutationsAreRejected()
    {
        await using var app = new PaymentApplication("unused", environment: "Development", settings: Enabled);
        using var client = app.CreateClient();
        await CheckoutApiTests.Error(await client.PostAsJsonAsync("/dev/payments/trips", new { finalFare = 725.50m }), 403, "DEV_ORIGIN_REJECTED");
        client.DefaultRequestHeaders.Add("X-GoRide-Dev", "1");
        client.DefaultRequestHeaders.Add("Origin", "https://evil.test");
        await CheckoutApiTests.Error(await client.PostAsJsonAsync("/dev/payments/trips", new { finalFare = 725.50m }), 403, "DEV_ORIGIN_REJECTED");
    }

    [MySqlFact]
    public async Task DevelopmentPageCreatesTestRideAndOpensStripeWithoutIdentityService()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub();
        await using var app = new PaymentApplication(db.ConnectionString, stripe, "Development", Enabled);
        using var client = app.CreateClient();
        // Even if another app's identity cookie is present, a local dev fixture never needs identity-auth.
        client.DefaultRequestHeaders.Add("Cookie", "session=unavailable");
        client.DefaultRequestHeaders.Add("X-GoRide-Dev", "1");
        var create = await client.PostAsJsonAsync("/dev/payments/trips", new { finalFare = 725.50m });
        create.EnsureSuccessStatusCode();
        var trip = (await create.Content.ReadFromJsonAsync<PaymentRecord>())!;
        Assert.StartsWith("dev-trip-", trip.TripId);
        Assert.Equal("Card", trip.Method);
        var checkout = await client.PostAsJsonAsync($"/dev/payments/trips/{trip.TripId}/checkout", new { });
        checkout.EnsureSuccessStatusCode();
        Assert.Equal(725.50m, (await checkout.Content.ReadFromJsonAsync<CheckoutRedirect>())!.Amount);
        Assert.Equal("Pending", (await client.GetFromJsonAsync<PaymentRecord>($"/dev/payments/trips/{trip.TripId}"))!.Status);
        var publicResponse = await client.PostAsJsonAsync($"/payments/{trip.TripId}/checkout", new { });
        await CheckoutApiTests.Error(publicResponse, 503, "IDENTITY_UNAVAILABLE");
    }

    [MySqlFact]
    public async Task DevelopmentFixtureCannotCheckoutAnotherRidersTrip()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub();
        await using var app = new PaymentApplication(db.ConnectionString, stripe, "Development", Enabled);
        using var client = CheckoutApiTests.RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await CheckoutApiTests.Seed(client, evt);
        client.DefaultRequestHeaders.Add("X-GoRide-Dev", "1");
        await CheckoutApiTests.Error(await client.PostAsJsonAsync($"/dev/payments/trips/{evt.TripId}/checkout", new { }), 403, "PAYMENT_FORBIDDEN");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/dev/payments/trips/{evt.TripId}")).StatusCode);
        Assert.Equal(0, stripe.Count);
    }
}
