using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GoRide.Payment.Cards;
using GoRide.Payment.Models;
using GoRide.Payment.Receipts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using MySqlConnector;
using Xunit;

namespace GoRide.Payment.Tests;

// In-app demo card payments end to end against MySQL: saved cards, Stripe-like charges and
// declines, the confirmation and email receipt, cash with driver confirmation, and the
// local-only completion of simulated rides.
public sealed class CardApiTests
{
    [MySqlFact]
    public async Task CardPaymentEmailsTheRegisteredAddressOnceThroughBrevo()
    {
        await using var db = await TestDatabase.CreateAsync();
        var mail = new BrevoCapture();
        await using var app = new PaymentApplication(db.ConnectionString, settings: new()
        {
            ["Email:Provider"] = "Brevo",
            ["Email:Brevo:ApiKey"] = "integration-test-key",
            ["Email:FromAddress"] = "receipts@goride.test"
        }).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient("Brevo").ConfigurePrimaryHttpMessageHandler(() => mail)));
        using var rider = app.CreateClient();
        rider.DefaultRequestHeaders.Add("Cookie", "session=rider-1-email");
        var evt = PaymentRulesTests.Completion();
        (await CheckoutApiTests.PostEvent(rider, evt)).EnsureSuccessStatusCode();
        var card = (await AddCard(rider, "4242424242424242")).GetProperty("cardId").GetString();
        for (var i = 0; i < 2; i++)
            (await rider.PostAsJsonAsync($"/payments/{evt.TripId}/pay", new { cardId = card })).EnsureSuccessStatusCode();
        var dispatcher = app.Services.GetRequiredService<ReceiptDispatcher>();
        Assert.True(await dispatcher.SendNextAsync(default));
        Assert.False(await dispatcher.SendNextAsync(default));
        Assert.Equal(1, mail.Requests);
        Assert.Equal(TestRider.Email, mail.Body.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Contains("725.50", mail.Body.GetProperty("textContent").GetString());
        Assert.Contains("4242", mail.Body.GetProperty("textContent").GetString());
        var receipt = await rider.GetFromJsonAsync<JsonElement>($"/payments/{evt.TripId}/receipt");
        Assert.Equal("Sent", receipt.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, receipt.GetProperty("sentAt").ValueKind);
    }

    private sealed class BrevoCapture : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public JsonElement Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            return new(HttpStatusCode.Created) { Content = JsonContent.Create(new { messageId = "test-receipt-message" }) };
        }
    }

    [MySqlFact]
    public async Task CardsAreSavedPerRiderWithOnlyBrandLastFourAndExpiry()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var rider = Client(app, "rider-1");
        var visa = await AddCard(rider, "4242 4242 4242 4242");
        Assert.True(visa.GetProperty("isDefault").GetBoolean());
        Assert.Equal("4242", visa.GetProperty("last4").GetString());
        var cards = await Cards(rider);
        Assert.Single(cards);
        await CheckoutApiTests.Error(await rider.PostAsJsonAsync("/payments/cards", Card("5555555555554444")), 409, "CARD_LIMIT_REACHED");

        // The full number is never stored or returned.
        Assert.Equal(0L, await Scalar(db, "SELECT COUNT(*) FROM payment_cards WHERE CONCAT_WS('|', card_id, last4, fingerprint, holder_name) LIKE '%4242424242424242%'"));
        Assert.DoesNotContain("4242424242424242", JsonSerializer.Serialize(cards));

        await CheckoutApiTests.Error(await rider.PostAsJsonAsync("/payments/cards", Card("4242424242424242")), 409, "CARD_LIMIT_REACHED");
        using (var other = Client(app, "other"))
        {
            Assert.Empty(await Cards(other));
            await CheckoutApiTests.Error(await other.DeleteAsync($"/payments/cards/{visa.GetProperty("cardId").GetString()}"), 404, "CARD_NOT_FOUND");
            await CheckoutApiTests.Error(await other.PostAsJsonAsync($"/payments/cards/{visa.GetProperty("cardId").GetString()}/default", new { }), 404, "CARD_NOT_FOUND");
        }
        await CheckoutApiTests.Error(await rider.DeleteAsync("/payments/cards/not-a-card"), 404, "CARD_NOT_FOUND");

        // Removing the only card allows a manually saved replacement.
        Assert.Equal(HttpStatusCode.NoContent, (await rider.DeleteAsync($"/payments/cards/{visa.GetProperty("cardId").GetString()}")).StatusCode);
        Assert.Empty(await Cards(rider));
        var replacement = await AddCard(rider, "5555555555554444");
        Assert.True(replacement.GetProperty("isDefault").GetBoolean());
        Assert.Single(await Cards(rider));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rider.GetAsync("/payments/cards/test-cards")).StatusCode);
    }

    [MySqlFact]
    public async Task ConcurrentFirstCardSavesKeepOnlyOneCard()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var rider = Client(app, "rider-1");
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            rider.PostAsJsonAsync("/payments/cards", Card(i % 2 == 0 ? "4242424242424242" : "5555555555554444"))));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var saved = Assert.Single(await Cards(rider));
        Assert.True(saved.GetProperty("isDefault").GetBoolean());
        Assert.Equal(1L, await db.Count("payment_cards"));
        foreach (var response in responses) response.Dispose();
    }

    [MySqlFact]
    public async Task WrongCardDetailsReturnFieldCodesAndNothingIsSaved()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var rider = Client(app, "rider-1");
        foreach (var (body, code) in new (object Body, string Code)[]
        {
            (Card("4242 4242 4242 4241"), "CARD_NUMBER_INVALID"),
            (Card("4111111111111111"), "CARD_NOT_TEST_CARD"),
            (Card("4242424242424242", expMonth: 13), "CARD_EXPIRY_INVALID"),
            (Card("4242424242424242", expYear: 2020), "CARD_EXPIRED"),
            (Card("4242424242424242", cvc: "12"), "CARD_CVC_INVALID"),
            (new { number = "4242424242424242", expMonth = 12, expYear = 2030, cvc = "123", riderId = "someone" }, "INVALID_REQUEST")
        })
        {
            using var response = await rider.PostAsJsonAsync("/payments/cards", body);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(400, (int)response.StatusCode);
            Assert.Equal(code, JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("4242424242424242", text);
        }
        Assert.Equal(0L, await db.Count("payment_cards"));
        using var anonymous = app.CreateClient();
        await CheckoutApiTests.Error(await anonymous.GetAsync("/payments/cards"), 401, "AUTHENTICATION_REQUIRED");
    }

    [MySqlFact]
    public async Task PayingWithASavedCardMarksPaidAndSendsExactlyOneReceipt()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var rider = Client(app, "rider-1-email");
        var evt = PaymentRulesTests.Completion();
        (await CheckoutApiTests.PostEvent(rider, evt)).EnsureSuccessStatusCode();
        var card = (await AddCard(rider, "4242424242424242")).GetProperty("cardId").GetString();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => rider.PostAsJsonAsync($"/payments/{evt.TripId}/pay", new { cardId = card })));
        foreach (var response in results) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bodies = await Task.WhenAll(results.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Single(bodies, b => !b.GetProperty("alreadyPaid").GetBoolean());
        var confirmation = bodies[0].GetProperty("confirmation");
        Assert.Equal(725.50m, confirmation.GetProperty("amount").GetDecimal());
        Assert.Equal("VISA", confirmation.GetProperty("cardBrand").GetString());
        Assert.Equal("4242", confirmation.GetProperty("cardLast4").GetString());
        Assert.StartsWith("demo_", confirmation.GetProperty("providerReference").GetString());
        Assert.Equal(1L, await db.Count("payment_confirmations"));
        Assert.Equal(1L, await db.Count("payment_receipts"));

        var status = await rider.GetFromJsonAsync<JsonElement>($"/payments/{evt.TripId}/status");
        Assert.Equal("Paid", status.GetProperty("status").GetString());
        Assert.Equal("Card", status.GetProperty("method").GetString());
        Assert.Equal("4242", status.GetProperty("cardLast4").GetString());

        Assert.True(await app.Services.GetRequiredService<ReceiptDispatcher>().SendNextAsync(default));
        var receipt = await rider.GetFromJsonAsync<JsonElement>($"/payments/{evt.TripId}/receipt");
        Assert.Equal("Logged", receipt.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("sentAt").ValueKind);
        var email = LogEmailSender.Find(receipt.GetProperty("receiptId").GetString()!)!;
        Assert.Equal(TestRider.Email, email.To);
        Assert.Contains("Paid with: VISA ending 4242", email.Text);
        Assert.Contains("Payment reference: demo_", email.Text);
        Assert.DoesNotContain("PayHere", email.Text);
    }

    [MySqlFact]
    public async Task DeclinedCardsLeaveTheTripUnpaidAndAnotherCardCanPay()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var rider = Client(app, "rider-1-email");
        var evt = PaymentRulesTests.Completion();
        (await CheckoutApiTests.PostEvent(rider, evt)).EnsureSuccessStatusCode();
        foreach (var (number, code) in new[]
        {
            ("4000000000000002", "CARD_DECLINED"), ("4000000000009995", "INSUFFICIENT_FUNDS"),
            ("4000000000000069", "EXPIRED_CARD"), ("4000000000000127", "INCORRECT_CVC"), ("4000000000000119", "PROCESSING_ERROR")
        })
        {
            var card = (await AddCard(rider, number)).GetProperty("cardId").GetString();
            await CheckoutApiTests.Error(await rider.PostAsJsonAsync($"/payments/{evt.TripId}/pay", new { cardId = card }), 402, code);
            await rider.DeleteAsync($"/payments/cards/{card}");
        }
        Assert.Equal("Pending", (await rider.GetFromJsonAsync<PaymentRecord>($"/payments/{evt.TripId}"))!.Status);
        Assert.Equal(0L, await db.Count("payment_confirmations"));
        Assert.Equal(0L, await db.Count("payment_receipts"));
        Assert.Equal(5L, await Scalar(db, "SELECT COUNT(*) FROM payment_verifications WHERE outcome = 'Failed'"));

        var good = (await AddCard(rider, "5555555555554444")).GetProperty("cardId").GetString();
        using var paid = await rider.PostAsJsonAsync($"/payments/{evt.TripId}/pay", new { cardId = good });
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.Equal("MASTERCARD", (await paid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmation").GetProperty("cardBrand").GetString());
    }

    [MySqlFact]
    public async Task PayRejectsOtherRidersCardsTripsAndUnknownFields()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var rider = Client(app, "rider-1");
        using var other = Client(app, "other");
        var evt = PaymentRulesTests.Completion();
        var card = (await AddCard(rider, "4242424242424242")).GetProperty("cardId").GetString();
        var othersCard = (await AddCard(other, "4242424242424242")).GetProperty("cardId").GetString();
        await CheckoutApiTests.Error(await rider.PostAsJsonAsync($"/payments/{evt.TripId}/pay", new { cardId = card }), 409, "TRIP_NOT_COMPLETED");
        (await CheckoutApiTests.PostEvent(rider, evt)).EnsureSuccessStatusCode();
        await CheckoutApiTests.Error(await other.PostAsJsonAsync($"/payments/{evt.TripId}/pay", new { cardId = othersCard }), 403, "PAYMENT_FORBIDDEN");
        await CheckoutApiTests.Error(await rider.PostAsJsonAsync($"/payments/{evt.TripId}/pay", new { cardId = othersCard }), 404, "CARD_NOT_FOUND");
        await CheckoutApiTests.Error(await rider.PostAsJsonAsync($"/payments/{evt.TripId}/pay", new { cardId = "nope" }), 404, "CARD_NOT_FOUND");
        await CheckoutApiTests.Error(await rider.PostAsJsonAsync($"/payments/{evt.TripId}/pay", new { cardId = card, amount = 1 }), 400, "INVALID_REQUEST");
        await CheckoutApiTests.Error(await other.GetAsync($"/payments/{evt.TripId}/status"), 403, "PAYMENT_FORBIDDEN");
        Assert.Equal(0L, await db.Count("payment_confirmations"));
        Assert.Equal("null", await (await rider.GetAsync($"/payments/{Guid.NewGuid()}/status")).Content.ReadAsStringAsync());
    }

    [MySqlFact]
    public async Task CashIsPaidOnlyWhenTheDriverConfirmsIt()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var rider = Client(app, "rider-1");
        using var driver = Client(app, "driver-1");
        var evt = PaymentRulesTests.Completion();
        (await CheckoutApiTests.PostEvent(rider, evt)).EnsureSuccessStatusCode();
        var waiting = await driver.GetFromJsonAsync<JsonElement>($"/payments/{evt.TripId}/status");
        Assert.Equal("Pending", waiting.GetProperty("status").GetString());
        Assert.Equal(725.50m, waiting.GetProperty("amount").GetDecimal());

        await CheckoutApiTests.Error(await driver.PostAsJsonAsync($"/payments/{evt.TripId}/cash/confirm", new { }), 409, "CASH_NOT_SELECTED");
        using (var cash = await rider.PostAsJsonAsync($"/payments/{evt.TripId}/cash", new { }))
            Assert.Equal("AwaitingCash", (await cash.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        var card = (await AddCard(rider, "4242424242424242")).GetProperty("cardId").GetString();
        await CheckoutApiTests.Error(await rider.PostAsJsonAsync($"/payments/{evt.TripId}/pay", new { cardId = card }), 409, "PAYMENT_NOT_PENDING");
        await CheckoutApiTests.Error(await rider.PostAsJsonAsync($"/payments/{evt.TripId}/cash/confirm", new { }), 403, "PAYMENT_FORBIDDEN");

        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var confirmed = await driver.PostAsync($"/payments/{evt.TripId}/cash/confirm", null);
            var body = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Paid", body.GetProperty("status").GetString());
            Assert.Equal("Cash", body.GetProperty("method").GetString());
        }
        await CheckoutApiTests.Error(await rider.PostAsJsonAsync($"/payments/{evt.TripId}/cash", new { }), 409, "PAYMENT_SETTLED");
        // Cash has no card receipt.
        Assert.Equal(0L, await db.Count("payment_receipts"));
    }

    [MySqlFact]
    public async Task SimulatedRidesCanOnlyBeCompletedLocallyByTheirRider()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using (var testing = new PaymentApplication(db.ConnectionString, settings: new() { ["DemoTrips:Enabled"] = "true" }))
        using (var client = Client(testing, "rider-1"))
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/payments/demo-completions", new { tripId = "trp_demo-1", finalFare = 640m })).StatusCode);

        await using var app = new PaymentApplication(db.ConnectionString, "Development", new() { ["DemoTrips:Enabled"] = "true" });
        using var rider = Client(app, "rider-1-email");
        var trip = "trp_" + Guid.NewGuid();
        await CheckoutApiTests.Error(await rider.PostAsJsonAsync("/payments/demo-completions", new { tripId = "trip-123", finalFare = 640m }), 400, "INVALID_REQUEST");
        await CheckoutApiTests.Error(await rider.PostAsJsonAsync("/payments/demo-completions", new { tripId = trip, finalFare = 0.001m }), 400, "INVALID_FARE");
        for (var i = 0; i < 2; i++)
        {
            using var created = await rider.PostAsJsonAsync("/payments/demo-completions", new { tripId = trip, finalFare = 640m });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            Assert.Equal("rider-1", (await created.Content.ReadFromJsonAsync<PaymentRecord>())!.RiderId);
        }
        using (var other = Client(app, "other"))
            await CheckoutApiTests.Error(await other.PostAsJsonAsync("/payments/demo-completions", new { tripId = trip, finalFare = 640m }), 403, "PAYMENT_FORBIDDEN");
        var card = (await AddCard(rider, "4242424242424242")).GetProperty("cardId").GetString();
        using var paid = await rider.PostAsJsonAsync($"/payments/{trip}/pay", new { cardId = card });
        Assert.Equal(640m, (await paid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmation").GetProperty("amount").GetDecimal());
    }

    private static HttpClient Client(PaymentApplication app, string session)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "session=" + session);
        return client;
    }

    private static object Card(string number, int expMonth = 12, int expYear = 2030, string cvc = "123") =>
        new { number, expMonth, expYear, cvc, holderName = "Rider One" };

    private static async Task<JsonElement> AddCard(HttpClient client, string number, bool makeDefault = false)
    {
        using var response = await client.PostAsJsonAsync("/payments/cards",
            new { number, expMonth = 12, expYear = 2030, cvc = "123", holderName = "Rider One", makeDefault });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement[]> Cards(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/payments/cards")).GetProperty("cards").EnumerateArray().ToArray();

    private static async Task<long> Scalar(TestDatabase db, string sql)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
