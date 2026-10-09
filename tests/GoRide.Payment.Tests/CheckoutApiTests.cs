using System.Net;
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
    public async Task CheckoutUsesFinalFareAndReusesOneSessionAcrossConcurrentRequestsAndRestart()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub();
        var evt = PaymentRulesTests.Completion();
        CheckoutRedirect first;
        await using (var app = new PaymentApplication(db.ConnectionString, stripe))
        using (var client = RiderClient(app))
        {
            await Seed(client, evt);
            var checkouts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Checkout(client, evt.TripId!)));
            Assert.Single(checkouts.Select(c => c.SessionId).Distinct());
            first = checkouts[0];
            Assert.Equal(725.50m, first.Amount);
            Assert.Equal("LKR", first.Currency);
            Assert.Equal("72550", stripe.LastCreate!["line_items[0][price_data][unit_amount]"]);
            Assert.Equal("card", stripe.LastCreate["payment_method_types[0]"]);
            Assert.Equal("https://goride.test/return?tripId=" + evt.TripId, stripe.LastCreate["success_url"]);
            var saved = await client.GetFromJsonAsync<PaymentRecord>($"/payments/{evt.TripId}");
            Assert.Equal("Pending", saved!.Status);
            Assert.Equal(0, saved.CardAttemptCount);
            Assert.Null(saved.ProcessedAt);
        }
        await using var restarted = new PaymentApplication(db.ConnectionString, stripe);
        using var retry = RiderClient(restarted);
        Assert.Equal(first.SessionId, (await Checkout(retry, evt.TripId!)).SessionId);
        Assert.Equal(1, stripe.Count);
        Assert.Equal(1, stripe.CreateRequests);
        Assert.Equal(1L, await db.Count("payment_checkouts"));
    }

    [MySqlFact]
    public async Task LostProviderResponseRecoversSameIdempotencyKeyAfterRestart()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub { LoseNextCreateResponse = true };
        var evt = PaymentRulesTests.Completion();
        await using (var app = new PaymentApplication(db.ConnectionString, stripe))
        using (var client = RiderClient(app))
        {
            await Seed(client, evt);
            await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { }), 503, "CHECKOUT_PROVIDER_UNAVAILABLE");
        }
        await using var restarted = new PaymentApplication(db.ConnectionString, stripe);
        using var retry = RiderClient(restarted);
        await Checkout(retry, evt.TripId!);
        Assert.Equal(1, stripe.Count);
        Assert.Equal(2, stripe.CreateRequests);
        Assert.Equal(1L, await db.Count("payment_checkouts"));
    }

    [MySqlFact]
    public async Task UnknownOldAttemptIsNeverRecreatedAfterIdempotencyWindow()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub { LoseNextCreateResponse = true };
        await using var app = new PaymentApplication(db.ConnectionString, stripe);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { }), 503, "CHECKOUT_PROVIDER_UNAVAILABLE");
        await db.ExecuteAsync("UPDATE payment_checkouts SET created_at = @old", ("@old", DateTime.UtcNow.AddHours(-25)));
        await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { }), 409, "CHECKOUT_RECONCILIATION_REQUIRED");
        Assert.Equal(1, stripe.CreateRequests);
    }

    [MySqlFact]
    public async Task CompletedProviderSessionCannotStartAnotherPaymentEvenWhenAppIsPending()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub();
        await using var app = new PaymentApplication(db.ConnectionString, stripe);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        await Checkout(client, evt.TripId!);
        stripe.SetStatus("complete");
        await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { }), 409, "CHECKOUT_AWAITING_VERIFICATION");
        var correction = evt with { EventId = "corrected", OccurredAt = evt.OccurredAt.AddSeconds(2), Payload = new() { FinalFare = 900 } };
        await Error(await PostEvent(client, correction), 409, "CHECKOUT_AWAITING_VERIFICATION");
        Assert.Equal(725.50m, (await client.GetFromJsonAsync<PaymentRecord>($"/payments/{evt.TripId}"))!.FinalFare);
        Assert.Equal(1L, await db.Count("processed_payment_events"));
        Assert.Equal(1, stripe.Count);
    }

    [MySqlFact]
    public async Task FareCorrectionExpiresOldSessionBeforeNewAmountAndRejectsReusedEventId()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub();
        await using var app = new PaymentApplication(db.ConnectionString, stripe);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        var initial = await Checkout(client, evt.TripId!);
        var correction = evt with { EventId = "fare-correction", OccurredAt = evt.OccurredAt.AddSeconds(1), Payload = new() { FinalFare = 899.29m } };
        await Error(await PostEvent(client, correction with { EventId = evt.EventId }), 409, "EVENT_ID_CONFLICT");
        Assert.Equal(0, stripe.Expirations);
        (await PostEvent(client, correction)).EnsureSuccessStatusCode();
        (await PostEvent(client, evt)).EnsureSuccessStatusCode();
        var updated = await Checkout(client, evt.TripId!);
        Assert.NotEqual(initial.SessionId, updated.SessionId);
        Assert.Equal(899.29m, updated.Amount);
        Assert.Equal(1, stripe.Expirations);
        Assert.Equal(2, stripe.Count);
        Assert.Equal(2L, await db.Count("payment_checkouts"));
        Assert.Equal("Card", (await client.GetFromJsonAsync<PaymentRecord>($"/payments/{evt.TripId}"))!.Method);
    }

    [MySqlFact]
    public async Task ProviderFailureDuringExpirationPreservesFareAndRetriesSameCorrection()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub();
        await using var app = new PaymentApplication(db.ConnectionString, stripe);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        await Checkout(client, evt.TripId!);
        stripe.FailExpiration = true;
        var correction = evt with { EventId = "correction", OccurredAt = evt.OccurredAt.AddSeconds(1), Payload = new() { FinalFare = 999 } };
        await Error(await PostEvent(client, correction), 503, "CHECKOUT_PROVIDER_UNAVAILABLE");
        Assert.Equal(725.50m, (await client.GetFromJsonAsync<PaymentRecord>($"/payments/{evt.TripId}"))!.FinalFare);
        Assert.Equal(1L, await db.Count("processed_payment_events"));
        stripe.FailExpiration = false;
        (await PostEvent(client, correction)).EnsureSuccessStatusCode();
        Assert.Equal(999m, (await Checkout(client, evt.TripId!)).Amount);
    }

    [MySqlFact]
    public async Task FareCorrectionRecoversUnknownProviderResultBeforeExpiringIt()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub { LoseNextCreateResponse = true };
        await using var app = new PaymentApplication(db.ConnectionString, stripe);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { }), 503, "CHECKOUT_PROVIDER_UNAVAILABLE");
        var correction = evt with { EventId = "correction", OccurredAt = evt.OccurredAt.AddSeconds(1), Payload = new() { FinalFare = 900 } };
        (await PostEvent(client, correction)).EnsureSuccessStatusCode();
        Assert.Equal(1, stripe.Count);
        Assert.Equal(1, stripe.Expirations);
        Assert.Equal(900m, (await Checkout(client, evt.TripId!)).Amount);
    }

    [MySqlFact]
    public async Task ExpiredSessionCanBeReplacedButSameFareRedeliveryKeepsActiveSession()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub();
        await using var app = new PaymentApplication(db.ConnectionString, stripe);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        var first = await Checkout(client, evt.TripId!);
        (await PostEvent(client, evt with { EventId = "new-delivery", OccurredAt = evt.OccurredAt.AddSeconds(1) })).EnsureSuccessStatusCode();
        Assert.Equal(first.SessionId, (await Checkout(client, evt.TripId!)).SessionId);
        stripe.SetStatus("expired");
        Assert.NotEqual(first.SessionId, (await Checkout(client, evt.TripId!)).SessionId);
        Assert.Equal(2, stripe.Count);
    }

    [MySqlFact]
    public async Task OnlyAuthenticatedOwnerOfCompletedCardSelectedTripCanCheckout()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub();
        await using var app = new PaymentApplication(db.ConnectionString, stripe);
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
        Assert.Equal(0, stripe.Count);
        Assert.Equal(0L, await db.Count("payment_checkouts"));
    }

    [MySqlFact]
    public async Task CallerCannotOverrideFareUrlsCurrencyIdentityOrSubmitCardDetails()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub();
        await using var app = new PaymentApplication(db.ConnectionString, stripe);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        foreach (var field in new[] { "amount", "currency", "returnUrl", "successUrl", "riderId", "cardNumber" })
            await Error(await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new Dictionary<string, string> { [field] = "untrusted" }), 400, "INVALID_REQUEST");
        foreach (var json in new[] { "null", "{" })
            await Error(await client.PostAsync($"/payments/{evt.TripId}/checkout", new StringContent(json, Encoding.UTF8, "application/json")), 400, "INVALID_REQUEST");
        Assert.Equal(0, stripe.Count);
    }

    [MySqlFact]
    public async Task SettledDisabledAndZeroFarePaymentsNeverReachStripe()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub();
        await using var app = new PaymentApplication(db.ConnectionString, stripe);
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
        Assert.Equal(0, stripe.Count);
    }

    [MySqlFact]
    public async Task UnsafeProviderUrlFailsClosedAndNeverReturnsRedirect()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var stripe = new StripeStub { UnsafeUrl = true };
        await using var app = new PaymentApplication(db.ConnectionString, stripe);
        using var client = RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await Seed(client, evt);
        var response = await client.PostAsJsonAsync($"/payments/{evt.TripId}/checkout", new { });
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain("evil.test", await response.Content.ReadAsStringAsync());
        await Error(response, 502, "INVALID_CHECKOUT_RESPONSE");
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

    internal static async Task<CheckoutRedirect> Checkout(HttpClient client, string trip)
    {
        using var response = await client.PostAsJsonAsync($"/payments/{trip}/checkout", new { });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<CheckoutRedirect>())!;
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
