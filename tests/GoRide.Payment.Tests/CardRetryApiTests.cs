using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GoRide.Payment.Cards;
using GoRide.Payment.Models;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Xunit;

namespace GoRide.Payment.Tests;

public sealed class CardRetryApiTests
{
    [MySqlFact]
    public async Task TransientThenSuccessCommitsOneChargeWithTwoAuditedAttemptsAtTheFinalFare()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var rider = Rider(app);
        await using var otherInstance = new PaymentApplication(db.ConnectionString);
        using var otherClient = Rider(otherInstance);
        var evt = PaymentRulesTests.Completion("trip-1");
        (await CheckoutApiTests.PostEvent(rider, evt)).EnsureSuccessStatusCode();
        var correction = evt with { EventId = "corrected", OccurredAt = evt.OccurredAt.AddSeconds(1), Payload = new() { FinalFare = 812.37m } };
        (await CheckoutApiTests.PostEvent(rider, correction)).EnsureSuccessStatusCode();
        var card = await Save(rider, "4000000000000341");
        var key = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Pay(i % 2 == 0 ? rider : otherClient, card, i < 3 ? key : Guid.NewGuid().ToString())));
        foreach (var response in responses)
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, body.GetProperty("attempts").GetInt32());
            Assert.True(body.GetProperty("autoRetried").GetBoolean());
            Assert.Equal(812.37m, body.GetProperty("confirmation").GetProperty("amount").GetDecimal());
            response.Dispose();
        }
        (await CheckoutApiTests.PostEvent(rider, evt)).EnsureSuccessStatusCode();
        (await CheckoutApiTests.PostEvent(rider, evt with { EventId = "redelivered" })).EnsureSuccessStatusCode();
        Assert.Equal(1L, await db.Count("payments"));
        Assert.Equal(1L, await db.Count("payment_card_requests"));
        Assert.Equal(1L, await db.Count("payment_confirmations"));
        Assert.Equal(1L, await db.Count("payment_receipts"));
        Assert.Equal(1L, await Scalar(db, "SELECT COUNT(*) FROM payment_verifications WHERE outcome = 'Paid'"));
        await AssertAttempts(db, 2, 81237);
        var status = await rider.GetFromJsonAsync<JsonElement>("/payments/trip-1/status");
        Assert.Equal(2, status.GetProperty("attemptCount").GetInt32());
        Assert.Equal("PROCESSING_ERROR", status.GetProperty("lastFailureCode").GetString());
        Assert.Equal("0341", status.GetProperty("cardLast4").GetString());
        Assert.DoesNotContain("4000000000000341", status.ToString());
        await db.ApplySchemaAsync();
        await db.ApplySchemaAsync();
        await AssertAttempts(db, 2, 81237);
    }

    [MySqlFact]
    public async Task EveryDeclineUsesItsRetryPolicyAndReplaysAcrossRestartAfterCardRemoval()
    {
        foreach (var (number, code, count) in new[]
        {
            ("4000000000000119", "PROCESSING_ERROR", 2), ("4000000000000077", "PROVIDER_TIMEOUT", 2),
            ("4000000000000085", "PROVIDER_UNAVAILABLE", 2), ("4000000000000002", "CARD_DECLINED", 1),
            ("4000000000009995", "INSUFFICIENT_FUNDS", 1), ("4000000000000069", "EXPIRED_CARD", 1),
            ("4000000000000127", "INCORRECT_CVC", 1)
        })
        {
            await using var db = await TestDatabase.CreateAsync();
            var key = Guid.NewGuid().ToString();
            string card;
            await using (var app = new PaymentApplication(db.ConnectionString))
            using (var rider = Rider(app))
            {
                (await CheckoutApiTests.PostEvent(rider, PaymentRulesTests.Completion("trip-1"))).EnsureSuccessStatusCode();
                card = await Save(rider, number);
                var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Pay(rider, card, key)));
                foreach (var response in responses) await AssertFailure(response, code, count);
                (await rider.DeleteAsync($"/payments/cards/{card}")).EnsureSuccessStatusCode();
            }
            await using var restarted = new PaymentApplication(db.ConnectionString);
            using var retry = Rider(restarted);
            await AssertFailure(await Pay(retry, card, key), code, count);
            Assert.Equal(0L, await db.Count("payment_confirmations"));
            Assert.Equal(0L, await db.Count("payment_receipts"));
            await AssertAttempts(db, count, 72550);
            var payment = await retry.GetFromJsonAsync<PaymentRecord>("/payments/trip-1");
            Assert.Equal(count, payment!.CardAttemptCount);
            Assert.False(payment.CardDisabled);
            Assert.Equal("Pending", payment.Status);
            var status = await retry.GetFromJsonAsync<JsonElement>("/payments/trip-1/status");
            Assert.Equal("Failed", status.GetProperty("requestState").GetString());
            Assert.Equal(code, status.GetProperty("lastFailureCode").GetString());
            Assert.Equal(count == 2, status.GetProperty("retryable").GetBoolean());
            var replacement = await Save(retry, "4242424242424242");
            await CheckoutApiTests.Error(await Pay(retry, replacement, key), 409, "PAYMENT_REQUEST_CONFLICT");
            (await Pay(retry, replacement, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();
            Assert.Equal(count + 1, await db.Count("payment_card_attempts"));
            Assert.Equal(1L, await db.Count("payment_confirmations"));
        }
    }

    [MySqlFact]
    public async Task ManualRetryWithTheSameCardIsANewBoundedRequest()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var rider = Rider(app);
        (await CheckoutApiTests.PostEvent(rider, PaymentRulesTests.Completion("trip-1"))).EnsureSuccessStatusCode();
        var card = await Save(rider, "4000000000000119");
        for (var i = 0; i < 3; i++) await AssertFailure(await Pay(rider, card, Guid.NewGuid().ToString()), "PROCESSING_ERROR", 2);
        Assert.Equal(6L, await db.Count("payment_card_attempts"));
        Assert.Equal(3L, await db.Count("payment_card_requests"));
        Assert.Equal(0L, await db.Count("payment_confirmations"));
    }

    [MySqlFact]
    public async Task AManualRetryUsesACorrectedFareWithoutRewritingFailedAttemptAmounts()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var rider = Rider(app);
        var evt = PaymentRulesTests.Completion("trip-1");
        (await CheckoutApiTests.PostEvent(rider, evt)).EnsureSuccessStatusCode();
        var card = await Save(rider, "4000000000000119");
        await AssertFailure(await Pay(rider, card, Guid.NewGuid().ToString()), "PROCESSING_ERROR", 2);
        (await CheckoutApiTests.PostEvent(rider, evt with
        {
            EventId = "corrected",
            OccurredAt = evt.OccurredAt.AddSeconds(1),
            Payload = new() { FinalFare = 911.33m }
        })).EnsureSuccessStatusCode();
        (await rider.DeleteAsync($"/payments/cards/{card}")).EnsureSuccessStatusCode();
        var replacement = await Save(rider, "4242424242424242");
        using var response = await Pay(rider, replacement, Guid.NewGuid().ToString());
        response.EnsureSuccessStatusCode();
        Assert.Equal(911.33m, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmation").GetProperty("amount").GetDecimal());
        Assert.Equal(2L, await Scalar(db, "SELECT COUNT(*) FROM payment_card_attempts WHERE outcome = 'Failed' AND amount_minor = 72550"));
        Assert.Equal(1L, await Scalar(db, "SELECT COUNT(*) FROM payment_card_attempts WHERE outcome = 'Paid' AND amount_minor = 91133 AND attempt_number = 3 AND automatic = FALSE"));
    }

    [MySqlFact]
    public async Task InvalidRequestsDoNotStartAttemptsAndPaidNeedsNoSavedCard()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString);
        using var rider = Rider(app);
        (await CheckoutApiTests.PostEvent(rider, PaymentRulesTests.Completion("trip-1"))).EnsureSuccessStatusCode();
        var card = await Save(rider, "4242424242424242");
        foreach (var body in new object[]
        {
            new { cardId = card }, new { cardId = card, requestId = "bad" },
            new { cardId = card, requestId = Guid.NewGuid().ToString(), amount = 1 },
            new { cardId = card, requestId = Guid.NewGuid().ToString(), finalFare = 1 },
            new { cardId = card, requestId = Guid.NewGuid().ToString(), currency = "USD" }
        }) await CheckoutApiTests.Error(await rider.PostAsJsonAsync("/payments/trip-1/pay", body), 400, "INVALID_REQUEST");
        await CheckoutApiTests.Error(await Pay(rider, null, Guid.NewGuid().ToString()), 404, "CARD_NOT_FOUND");
        Assert.Equal(0L, await db.Count("payment_card_attempts"));
        (await Pay(rider, card, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();
        (await rider.DeleteAsync($"/payments/cards/{card}")).EnsureSuccessStatusCode();
        using var paid = await Pay(rider, null, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.True((await paid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("alreadyPaid").GetBoolean());
        Assert.Equal(1L, await db.Count("payment_card_attempts"));
    }

    [MySqlFact]
    public async Task InterruptedAttemptResumesItsIdentityAndBlocksFareCorrectionsAndNewRequests()
    {
        await using var db = await TestDatabase.CreateAsync();
        string card;
        var key = Guid.NewGuid().ToString();
        var evt = PaymentRulesTests.Completion("trip-1");
        await using (var app = new PaymentApplication(db.ConnectionString))
        using (var rider = Rider(app))
        {
            (await CheckoutApiTests.PostEvent(rider, evt)).EnsureSuccessStatusCode();
            card = await Save(rider, "4000000000000341");
            using var scope = app.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<CardAttemptStore>();
            var request = new CardChargeRequest("trip-1", key, card, "Visa", "0341", CardBehaviour.ProcessingOnce,
                "demo-" + Guid.NewGuid().ToString("N"), 72550, DateTimeOffset.UtcNow);
            await store.CreateAsync(request, default);
            await store.StartAsync(request, DateTimeOffset.UtcNow, default);
        }
        await using var restarted = new PaymentApplication(db.ConnectionString);
        using var retry = Rider(restarted);
        await CheckoutApiTests.Error(await Pay(retry, card, Guid.NewGuid().ToString()), 409, "PAYMENT_IN_PROGRESS");
        await CheckoutApiTests.Error(await CheckoutApiTests.PostEvent(retry, evt with
        {
            EventId = "correction",
            OccurredAt = evt.OccurredAt.AddSeconds(1),
            Payload = new() { FinalFare = 900m }
        }), 409, "PAYMENT_IN_PROGRESS");
        (await Pay(retry, card, key)).EnsureSuccessStatusCode();
        await AssertAttempts(db, 2, 72550);
        Assert.Equal(1L, await db.Count("payment_confirmations"));
    }

    [MySqlFact]
    public async Task StatusShowsRetryWhileTheSamePayRequestIsStillRunning()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString, settings: new() { ["DemoCard:RetryMilliseconds"] = "1000" });
        using var rider = Rider(app);
        (await CheckoutApiTests.PostEvent(rider, PaymentRulesTests.Completion("trip-1"))).EnsureSuccessStatusCode();
        var card = await Save(rider, "4000000000000341");
        var key = Guid.NewGuid().ToString();
        var paying = Pay(rider, card, key);
        var observed = false;
        for (var i = 0; i < 100 && !paying.IsCompleted; i++)
        {
            var status = await rider.GetFromJsonAsync<JsonElement>("/payments/trip-1/status");
            if (status.GetProperty("requestState").GetString() == "Retrying")
            {
                Assert.Equal(key, status.GetProperty("requestId").GetString());
                Assert.True(status.GetProperty("autoRetried").GetBoolean());
                observed = true;
                break;
            }
            await Task.Delay(10);
        }
        (await paying).EnsureSuccessStatusCode();
        Assert.True(observed);
    }

    private static HttpClient Rider(PaymentApplication app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "session=rider-1-email");
        return client;
    }

    private static async Task<string> Save(HttpClient client, string number)
    {
        using var response = await client.PostAsJsonAsync("/payments/cards", new { number, expMonth = 12, expYear = 2030, cvc = "123" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cardId").GetString()!;
    }

    private static Task<HttpResponseMessage> Pay(HttpClient client, string? cardId, string requestId) =>
        client.PostAsJsonAsync("/payments/trip-1/pay", new { cardId, requestId });

    private static async Task AssertFailure(HttpResponseMessage response, string code, int attempts)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(code, body.GetProperty("code").GetString());
            Assert.Equal(attempts, body.GetProperty("attempts").GetInt32());
            Assert.Equal(attempts == 2, body.GetProperty("autoRetried").GetBoolean());
            Assert.Equal(attempts == 2, body.GetProperty("retryable").GetBoolean());
        }
    }

    private static async Task AssertAttempts(TestDatabase db, int count, long amount)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT * FROM payment_card_attempts ORDER BY attempt_number", connection);
        await using var reader = await command.ExecuteReaderAsync();
        for (var i = 1; i <= count; i++)
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(i, reader.GetInt32("attempt_number"));
            Assert.Equal(i, reader.GetInt32("request_attempt"));
            Assert.Equal(i > 1, reader.GetBoolean("automatic"));
            Assert.Equal(amount, reader.GetInt64("amount_minor"));
            Assert.Equal("LKR", reader.GetString("currency"));
            Assert.True(reader.GetDateTime("completed_at") >= reader.GetDateTime("started_at"));
            Assert.Contains(reader.GetString("outcome"), new[] { "Paid", "Failed" });
        }
        Assert.False(await reader.ReadAsync());
    }

    private static async Task<long> Scalar(TestDatabase db, string sql)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
