using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GoRide.Payment.Checkout;
using GoRide.Payment.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GoRide.Payment.Tests;

public sealed class CheckoutApiTests
{
    [MySqlFact]
    public async Task CheckoutSignsFinalFareAndReusesOneOrderAcrossConcurrentRequestsAndRestart()
    {
        await using var db = await TestDatabase.CreateAsync();
        var evt = PaymentRulesTests.Completion();
        CheckoutForm first;
        await using (var app = new PaymentApplication(db.ConnectionString))
        using (var client = RiderClient(app))
        {
            await Seed(client, evt);
            var forms = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Checkout(client, evt.TripId!)));
            Assert.Single(forms.Select(f => f.OrderId).Distinct());
            first = forms[0];
            Assert.Equal(PayHereSettings.CheckoutUrl, first.ActionUrl);
            Assert.Equal(725.50m, first.Amount);
            Assert.Equal("LKR", first.Currency);
            Assert.Matches("^goride-[0-9a-f]{32}$", first.OrderId);
            Assert.Equal("725.50", first.Fields["amount"]);
            Assert.Equal("LKR", first.Fields["currency"]);
            Assert.Equal(TestPayHere.MerchantId, first.Fields["merchant_id"]);
            Assert.Equal("https://goride.test/return?tripId=" + evt.TripId, first.Fields["return_url"]);
            Assert.Equal("https://goride.test/payments/payhere/notify", first.Fields["notify_url"]);
            Assert.Equal(PayHereSignature.CheckoutHash(TestPayHere.MerchantId, first.OrderId, 72550, "LKR", TestPayHere.Secret), first.Fields["hash"]);
            var saved = await client.GetFromJsonAsync<PaymentRecord>($"/payments/{evt.TripId}");
            Assert.Equal("Pending", saved!.Status);
            Assert.Equal(0, saved.CardAttemptCount);
            Assert.Null(saved.ProcessedAt);
        }
        await using var restarted = new PaymentApplication(db.ConnectionString);
        using var retry = RiderClient(restarted);
        Assert.Equal(first.OrderId, (await Checkout(retry, evt.TripId!)).OrderId);
        Assert.Equal(1L, await db.Count("payment_checkouts"));
    }

    [MySqlFact]
    public async Task CheckoutResponseNeverContainsTheMerchantSecret()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        using var response = await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { });
        response.EnsureSuccessStatusCode();
        Assert.DoesNotContain(TestPayHere.Secret, await response.Content.ReadAsStringAsync());
    }

    [MySqlFact]
    public async Task FareCorrectionStartsNewOrderAndRejectsReusedEventId()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        var initial = await Checkout(client, evt.TripId!);
        var correction = evt with { EventId = "fare-correction", OccurredAt = evt.OccurredAt.AddSeconds(1), Payload = new() { FinalFare = 899.29m } };
        await Error(await PostEvent(client, correction with { EventId = evt.EventId }), 409, "EVENT_ID_CONFLICT");
        (await PostEvent(client, correction)).EnsureSuccessStatusCode();
        (await PostEvent(client, evt)).EnsureSuccessStatusCode();
        var updated = await Checkout(client, evt.TripId!);
        Assert.NotEqual(initial.OrderId, updated.OrderId);
        Assert.Equal(899.29m, updated.Amount);
        Assert.Equal("899.29", updated.Fields["amount"]);
        Assert.Equal(2L, await db.Count("payment_checkouts"));
        Assert.Equal("Card", (await client.GetFromJsonAsync<PaymentRecord>($"/payments/{evt.TripId}"))!.Method);
    }

    [MySqlFact]
    public async Task SameFareRedeliveryKeepsTheOpenOrder()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        var first = await Checkout(client, evt.TripId!);
        (await PostEvent(client, evt with { EventId = "new-delivery", OccurredAt = evt.OccurredAt.AddSeconds(1) })).EnsureSuccessStatusCode();
        Assert.Equal(first.OrderId, (await Checkout(client, evt.TripId!)).OrderId);
        Assert.Equal(1L, await db.Count("payment_checkouts"));
    }

    [MySqlFact]
    public async Task OnlyAuthenticatedOwnerOfCompletedCardSelectedTripCanCheckout()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var client = app.CreateClient();
        var evt = PaymentRulesTests.Completion();
        await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { }), 401, "AUTHENTICATION_REQUIRED");
        client.DefaultRequestHeaders.Add("Cookie", "session=rider-1");
        await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { }), 409, "TRIP_NOT_COMPLETED");
        (await PostEvent(client, evt)).EnsureSuccessStatusCode();
        await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { }), 409, "CARD_NOT_SELECTED");
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", "session=other");
        await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { }), 403, "PAYMENT_FORBIDDEN");
        Assert.Equal(0L, await db.Count("payment_checkouts"));
    }

    [MySqlFact]
    public async Task CallerCannotOverrideFareUrlsCurrencyIdentityOrSubmitCardDetails()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        foreach (var field in new[] { "amount", "currency", "returnUrl", "notifyUrl", "riderId", "orderId", "cardNumber" })
            await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new Dictionary<string, string> { [field] = "untrusted" }), 400, "INVALID_REQUEST");
        foreach (var json in new[] { "null", "{" })
            await Error(await client.PostAsync($"/payments/{evt.TripId}/checkout", new StringContent(json, Encoding.UTF8, "application/json")), 400, "INVALID_REQUEST");
        Assert.Equal(0L, await db.Count("payment_checkouts"));
    }

    [MySqlFact]
    public async Task SettledDisabledAndZeroFarePaymentsNeverGetACheckoutForm()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        foreach (var status in new[] { "Charged", "Paid" })
        {
            await db.ExecuteAsync("UPDATE payments SET document = JSON_SET(document, '$.status', @status)", ("@status", status));
            await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { }), 409, "PAYMENT_SETTLED");
        }
        await db.ExecuteAsync("UPDATE payments SET document = JSON_SET(document, '$.status', 'Pending', '$.cardAttemptCount', '2')");
        await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { }), 409, "CARD_DISABLED");
        var free = PaymentRulesTests.Completion() with { Payload = new() { FinalFare = 0 } };
        await Seed(client, free);
        await Error(await client.PostAsJsonAsync($"/payments/{free.TripId}/checkout", new { }), 409, "CHECKOUT_AMOUNT_UNSUPPORTED");
        Assert.Equal(0L, await db.Count("payment_checkouts"));
    }

    [MySqlFact]
    public async Task CheckoutRequiresSandboxCredentialsAndSafeUrls()
    {
        await using var db = await TestDatabase.CreateAsync();
        var evt = PaymentRulesTests.Completion();
        foreach (var (key, value, code) in new[]
        {
            ("PayHere:MerchantSecret", "", "PAYHERE_NOT_CONFIGURED"),
            ("PayHere:MerchantId", "not-a-merchant", "PAYHERE_NOT_CONFIGURED"),
            ("PayHere:NotifyUrl", "http://goride.test/notify", "CHECKOUT_NOT_CONFIGURED"),
            ("PayHere:ReturnUrl", "https://user:pass@goride.test/return", "CHECKOUT_NOT_CONFIGURED")
        })
        {
            await using var app = new PaymentApplication(db.ConnectionString, settings: new() { [key] = value });
            using var client = RiderClient(app);
            await Seed(client, evt);
            await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { }), 503, code);
        }
        Assert.Equal(0L, await db.Count("payment_checkouts"));
    }

    internal static HttpClient RiderClient(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "session=rider-1");
        return client;
    }

    internal static async Task Seed(HttpClient client, TripCompletedEvent evt)
    {
        (await PostEvent(client, evt)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/payments/{evt.TripId}/select-method", new { method = "Card" })).EnsureSuccessStatusCode();
    }

    internal static async Task<HttpResponseMessage> PostEvent(HttpClient client, TripCompletedEvent evt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/trip-events") { Content = JsonContent.Create(evt) };
        request.Headers.Add("X-Internal-Api-Key", "test-service-key");
        return await client.SendAsync(request);
    }

    internal static async Task<CheckoutForm> Checkout(HttpClient client, string trip)
    {
        using var response = await client.PostAsJsonAsync($"/payments/{trip}/checkout", new { });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<CheckoutForm>())!;
    }

    internal static async Task Error(HttpResponseMessage response, int status, string code)
    {
        using (response)
        {
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }
}
