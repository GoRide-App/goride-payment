using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GoRide.Payment.Checkout;
using GoRide.Payment.Models;
using GoRide.Payment.Receipts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GoRide.Payment.Tests;

// SCRUM-105 / SCRUM-681: receipts against MySQL. One receipt per paid trip however often
// PayHere redelivers, retried with backoff, and resent only within the limits.
public sealed class ReceiptApiTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    [MySqlFact]
    public async Task PaidCardPaymentSendsExactlyOneReceiptEvenWhenPayHereRedelivers()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString, clock: new ManualClock(Start));
        using var client = EmailRider(app);
        var (trip, form) = await StartCheckout(client);
        var notices = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Notify(client, form, "320027150001")));
        Assert.All(notices, notice => Assert.Equal(HttpStatusCode.OK, notice.StatusCode));
        Assert.Equal(1L, await db.Count("payment_receipts"));

        var queued = await Receipt(client, trip);
        Assert.Equal(ReceiptStatus.Pending, queued.Status);
        Assert.Equal("r***e@goride.lk", queued.Recipient);
        Assert.False(queued.CanResend);

        var dispatcher = Dispatcher(app);
        Assert.True(await dispatcher.SendNextAsync(default));
        Assert.False(await dispatcher.SendNextAsync(default));
        // A redelivery after the receipt went out neither creates nor sends another.
        (await Notify(client, form, "320027150001")).EnsureSuccessStatusCode();
        Assert.False(await dispatcher.SendNextAsync(default));
        Assert.Equal(1L, await db.Count("payment_receipts"));

        var sent = await Receipt(client, trip);
        Assert.Equal(ReceiptStatus.Sent, sent.Status);
        Assert.Equal(1, sent.Attempts);
        Assert.Equal(Start, sent.SentAt);
        Assert.Equal(Start + ReceiptRules.ResendCooldown, sent.ResendAvailableAt);
        var email = LogEmailSender.Find(sent.ReceiptId)!;
        Assert.Equal(TestRider.Email, email.To);
        Assert.Equal("Your GoRide receipt · LKR 725.50", email.Subject);
        Assert.Contains("Hi Rider,", email.Text);
        Assert.Contains("Paid with: VISA ending 1292", email.Text);
        Assert.Contains("PayHere reference: 320027150001", email.Text);
        Assert.Contains("Trip reference: " + trip, email.Text);
    }

    [MySqlFact]
    public async Task TransientEmailFailuresRetryWithBackoffThenStopAfterMaxAttempts()
    {
        await using var db = await TestDatabase.CreateAsync();
        var clock = new ManualClock(Start);
        // Brevo without an API key fails transiently, like a provider outage.
        await using var app = new PaymentApplication(db.ConnectionString, clock: clock,
            settings: new() { ["Email:Provider"] = "Brevo", ["Email:Brevo:ApiKey"] = "" });
        using var client = EmailRider(app);
        var (trip, form) = await StartCheckout(client);
        (await Notify(client, form, "320027150002")).EnsureSuccessStatusCode();
        var dispatcher = Dispatcher(app);
        for (var attempt = 1; attempt <= ReceiptRules.MaxDeliveryAttempts; attempt++)
        {
            Assert.True(await dispatcher.SendNextAsync(default));
            // Not due again until the backoff has passed.
            Assert.False(await dispatcher.SendNextAsync(default));
            var view = await Receipt(client, trip);
            Assert.Equal(attempt, view.Attempts);
            Assert.Equal(attempt < ReceiptRules.MaxDeliveryAttempts ? ReceiptStatus.Retry : ReceiptStatus.Failed, view.Status);
            clock.Advance(ReceiptRules.RetryDelay(attempt));
        }
        Assert.False(await dispatcher.SendNextAsync(default));
        var failed = await Receipt(client, trip);
        Assert.Null(failed.SentAt);
        Assert.True(failed.CanResend);
    }

    [MySqlFact]
    public async Task RiderCanResendAfterTheCooldownUpToTheLimitAndConcurrentRequestsQueueOneSend()
    {
        await using var db = await TestDatabase.CreateAsync();
        var clock = new ManualClock(Start);
        await using var app = new PaymentApplication(db.ConnectionString, clock: clock);
        using var client = EmailRider(app);
        var (trip, form) = await StartCheckout(client);
        (await Notify(client, form, "320027150003")).EnsureSuccessStatusCode();
        var dispatcher = Dispatcher(app);
        Assert.True(await dispatcher.SendNextAsync(default));

        using (var tooSoon = await Resend(client, trip))
        {
            Assert.Equal(429, (int)tooSoon.StatusCode);
            Assert.Equal(ReceiptRules.ResendCooldown, tooSoon.Headers.RetryAfter?.Delta);
            var body = await tooSoon.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("RECEIPT_RESEND_TOO_SOON", body.GetProperty("code").GetString());
            Assert.Equal(60, body.GetProperty("retryAfterSeconds").GetInt32());
        }
        for (var round = 1; round <= ReceiptRules.MaxResends; round++)
        {
            clock.Advance(ReceiptRules.ResendCooldown);
            var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Resend(client, trip)));
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
            Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.Accepted),
                response => Assert.Contains((int)response.StatusCode, new[] { 409, 429 }));
            foreach (var response in responses) response.Dispose();
            var queued = await Receipt(client, trip);
            Assert.Equal(ReceiptStatus.Pending, queued.Status);
            Assert.Equal(ReceiptRules.MaxResends - round, queued.ResendsLeft);
            Assert.True(await dispatcher.SendNextAsync(default));
            Assert.False(await dispatcher.SendNextAsync(default));
        }
        clock.Advance(ReceiptRules.ResendCooldown);
        await CheckoutApiTests.Error(await Resend(client, trip), 429, "RECEIPT_RESEND_LIMIT");
        var final = await Receipt(client, trip);
        Assert.Equal(ReceiptStatus.Sent, final.Status);
        Assert.False(final.CanResend);
        Assert.Equal(0, final.ResendsLeft);
        Assert.Equal(1L, await db.Count("payment_receipts"));
    }

    [MySqlFact]
    public async Task ReceiptEndpointsValidateTheCallerTripAndBody()
    {
        await using var db = await TestDatabase.CreateAsync();
        var clock = new ManualClock(Start);
        await using var app = new PaymentApplication(db.ConnectionString, clock: clock);
        using var client = EmailRider(app);
        var (trip, form) = await StartCheckout(client);
        await CheckoutApiTests.Error(await client.GetAsync($"/payments/{trip}/receipt"), 409, "RECEIPT_NOT_AVAILABLE");
        (await Notify(client, form, "320027150004")).EnsureSuccessStatusCode();
        Assert.True(await Dispatcher(app).SendNextAsync(default));
        clock.Advance(ReceiptRules.ResendCooldown);

        // The receipt always goes to the address captured at checkout; callers cannot redirect it.
        foreach (var field in new[] { "email", "recipient", "to", "riderId" })
            await CheckoutApiTests.Error(await client.PostAsJsonAsync($"/payments/{trip}/receipt/resend",
                new Dictionary<string, string> { [field] = "attacker@evil.test" }), 400, "INVALID_REQUEST");
        await CheckoutApiTests.Error(await client.PostAsync($"/payments/{trip}/receipt/resend",
            new StringContent("{", Encoding.UTF8, "application/json")), 400, "INVALID_REQUEST");
        await CheckoutApiTests.Error(await client.GetAsync("/payments/%20padded/receipt"), 400, "INVALID_REQUEST");
        await CheckoutApiTests.Error(await client.GetAsync($"/payments/{Guid.NewGuid()}/receipt"), 409, "TRIP_NOT_COMPLETED");
        using (var other = app.CreateClient())
        {
            other.DefaultRequestHeaders.Add("Cookie", "session=other");
            await CheckoutApiTests.Error(await other.GetAsync($"/payments/{trip}/receipt"), 403, "PAYMENT_FORBIDDEN");
            await CheckoutApiTests.Error(await Resend(other, trip), 403, "PAYMENT_FORBIDDEN");
        }
        using (var anonymous = app.CreateClient())
            await CheckoutApiTests.Error(await Resend(anonymous, trip), 401, "AUTHENTICATION_REQUIRED");

        // None of the rejected requests queued a send.
        Assert.False(await Dispatcher(app).SendNextAsync(default));
        var receipt = await Receipt(client, trip);
        Assert.Equal(ReceiptRules.MaxResends, receipt.ResendsLeft);
        Assert.True(receipt.CanResend);
        using var accepted = await client.PostAsync($"/payments/{trip}/receipt/resend", null);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.True(accepted.Headers.CacheControl?.NoStore);
    }

    [MySqlFact]
    public async Task RiderWithoutAnEmailOnTheAccountGetsNoEmailStatusAndCannotResend()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var client = CheckoutApiTests.RiderClient(app);
        var (trip, form) = await StartCheckout(client);
        (await Notify(client, form, "320027150005")).EnsureSuccessStatusCode();
        Assert.False(await Dispatcher(app).SendNextAsync(default));
        var receipt = await Receipt(client, trip);
        Assert.Equal(ReceiptStatus.NoEmail, receipt.Status);
        Assert.Null(receipt.Recipient);
        Assert.False(receipt.CanResend);
        await CheckoutApiTests.Error(await Resend(client, trip), 409, "RECEIPT_EMAIL_MISSING");
        // The PayHere placeholder address is never stored or emailed.
        Assert.Equal(0L, await db.Count("payment_contacts"));
    }

    [MySqlFact]
    public async Task ReceiptLeftSendingByACrashedSenderIsClaimedAgainAfterTheLease()
    {
        await using var db = await TestDatabase.CreateAsync();
        var clock = new ManualClock(Start);
        await using var app = new PaymentApplication(db.ConnectionString, clock: clock);
        using var client = EmailRider(app);
        var (trip, form) = await StartCheckout(client);
        (await Notify(client, form, "320027150006")).EnsureSuccessStatusCode();
        await db.ExecuteAsync("UPDATE payment_receipts SET status = 'Sending', attempts = 1, lease_token = 'crashed', lease_until = @until",
            ("@until", (Start + ReceiptRules.Lease).UtcDateTime));
        var dispatcher = Dispatcher(app);
        Assert.False(await dispatcher.SendNextAsync(default));
        await CheckoutApiTests.Error(await Resend(client, trip), 409, "RECEIPT_IN_PROGRESS");
        clock.Advance(ReceiptRules.Lease + TimeSpan.FromSeconds(1));
        Assert.True(await dispatcher.SendNextAsync(default));
        var receipt = await Receipt(client, trip);
        Assert.Equal(ReceiptStatus.Sent, receipt.Status);
        Assert.Equal(2, receipt.Attempts);
    }

    [MySqlFact]
    public async Task DevelopmentPageEmailsAReceiptForTheTestRideAndPreviewsIt()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString, "Development", new() { ["DevelopmentCheckout:Enabled"] = "true" });
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-GoRide-Dev", "1");
        await CheckoutApiTests.Error(await client.PostAsJsonAsync("/dev/payments/trips", new { finalFare = 725.50m, email = "not-an-email" }), 400, "INVALID_EMAIL");
        using var create = await client.PostAsJsonAsync("/dev/payments/trips", new { finalFare = 725.50m, email = "dev.rider@goride.test" });
        create.EnsureSuccessStatusCode();
        var trip = (await create.Content.ReadFromJsonAsync<PaymentRecord>())!.TripId;
        (await client.PostAsJsonAsync($"/dev/payments/trips/{trip}/checkout", new { })).EnsureSuccessStatusCode();
        await CheckoutApiTests.Error(await client.GetAsync($"/dev/payments/trips/{trip}/receipt"), 409, "RECEIPT_NOT_AVAILABLE");
        (await client.PostAsJsonAsync($"/dev/payments/trips/{trip}/simulate-notify", new { statusCode = 2 })).EnsureSuccessStatusCode();
        Assert.True(await Dispatcher(app).SendNextAsync(default));

        var receipt = (await client.GetFromJsonAsync<ReceiptView>($"/dev/payments/trips/{trip}/receipt"))!;
        Assert.Equal(ReceiptStatus.Sent, receipt.Status);
        Assert.Equal("d***r@goride.test", receipt.Recipient);
        using var preview = await client.GetAsync($"/dev/payments/trips/{trip}/receipt/preview");
        preview.EnsureSuccessStatusCode();
        Assert.Equal("text/html", preview.Content.Headers.ContentType?.MediaType);
        Assert.Contains("sandbox", preview.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("LKR 725.50", await preview.Content.ReadAsStringAsync());
        // Unknown fields are a structured 400 here too, never a server error.
        await CheckoutApiTests.Error(await client.PostAsJsonAsync($"/dev/payments/trips/{trip}/receipt/resend", new { email = "attacker@evil.test" }), 400, "INVALID_REQUEST");
        await CheckoutApiTests.Error(await client.PostAsJsonAsync("/dev/payments/trips", new { finalFare = 725.50m, riderId = "someone" }), 400, "INVALID_REQUEST");
    }

    private static HttpClient EmailRider(PaymentApplication app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "session=rider-1-email");
        return client;
    }

    private static ReceiptDispatcher Dispatcher(PaymentApplication app) => app.Services.GetRequiredService<ReceiptDispatcher>();

    private static async Task<(string Trip, CheckoutForm Form)> StartCheckout(HttpClient client)
    {
        var evt = PaymentRulesTests.Completion();
        await CheckoutApiTests.Seed(client, evt);
        return (evt.TripId!, await CheckoutApiTests.Checkout(client, evt.TripId!));
    }

    // The signed notice PayHere's server posts for a successful card payment.
    private static Task<HttpResponseMessage> Notify(HttpClient client, CheckoutForm form, string paymentId)
    {
        var amount = form.Fields["amount"];
        var currency = form.Fields["currency"];
        return client.PostAsync("/payments/payhere/notify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["merchant_id"] = TestPayHere.MerchantId,
            ["order_id"] = form.OrderId,
            ["payment_id"] = paymentId,
            ["payhere_amount"] = amount,
            ["payhere_currency"] = currency,
            ["status_code"] = "2",
            ["md5sig"] = PayHereSignature.NotifySignature(TestPayHere.MerchantId, form.OrderId, amount, currency, "2", TestPayHere.Secret),
            ["method"] = "VISA",
            ["card_no"] = "************1292"
        }));
    }

    private static async Task<ReceiptView> Receipt(HttpClient client, string trip)
    {
        using var response = await client.GetAsync($"/payments/{trip}/receipt");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<ReceiptView>())!;
    }

    private static Task<HttpResponseMessage> Resend(HttpClient client, string trip) =>
        client.PostAsJsonAsync($"/payments/{trip}/receipt/resend", new { });
}
