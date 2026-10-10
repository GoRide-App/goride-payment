using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GoRide.Payment.Checkout;
using GoRide.Payment.DriverNotifications;
using GoRide.Payment.Models;
using GoRide.Payment.Services;
using GoRide.Payment.Verification;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace GoRide.Payment.Tests;

public sealed class DriverNotificationRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T12:00:00Z");

    [Fact]
    public void SnapshotUsesPaidRecordInsteadOfEstimateOrNoticeAmountAndExposesOnlyLastFour()
    {
        var paid = PaymentRules.ApplyCompletion(null, PaymentRulesTests.Completion()) with
        {
            Method = "Card",
            Status = "Paid",
            ProcessedAt = Now,
            FinalFare = 921.37m
        };
        var notice = new VerificationRecord("PayHere", "provider-1", 2, "order-1", paid.TripId, 60000,
            "LKR", "hash", "VISA", "************1292", Now.AddMinutes(1));
        var content = DriverNotificationRules.ForPaid(paid, notice)!;
        Assert.Equal(paid.Id, content.PaymentId);
        Assert.Equal(paid.DriverId, content.DriverId);
        Assert.Equal(921.37m, content.Notification.Amount);
        Assert.Equal(Now, content.Notification.PaidAt);
        Assert.Equal("LKR", content.Notification.Currency);
        Assert.Equal("1292", content.Notification.CardLast4);
        Assert.Null(DriverNotificationRules.ForPaid(paid, notice with { CardMasked = "unknown" })!.Notification.CardLast4);
    }

    [Theory]
    [InlineData("Pending", "Card")]
    [InlineData("Failed", "Card")]
    [InlineData("Paid", "Cash")]
    [InlineData("Paid", null)]
    public void OnlyConfirmedPaidCardCreatesNotification(string status, string? method)
    {
        var payment = PaymentRules.ApplyCompletion(null, PaymentRulesTests.Completion()) with { Status = status, Method = method };
        Assert.Null(DriverNotificationRules.ForPaid(payment,
            new("DemoCard", "provider", 2, "order", payment.TripId, 72550, "LKR", "hash", null, null, Now)));
    }

    [Fact]
    public void SinceDefaultsToSevenDaysAndNormalizesExplicitOffset()
    {
        Assert.Equal(Now.AddDays(-7), DriverNotificationRules.Since(null, Now));
        Assert.Equal(Now, DriverNotificationRules.Since("2026-10-11T17:30:00+05:30", Now));
        Assert.Equal(Now.AddTicks(-1), DriverNotificationRules.Since("2026-10-11T11:59:59.9999999Z", Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-10-11")]
    [InlineData("2026-10-11T10:00:00")]
    [InlineData("yesterday")]
    [InlineData("2026-02-30T10:00:00Z")]
    [InlineData("2026-10-12T00:00:00Z")]
    public void InvalidSinceHasStructuredCode(string value)
    {
        var error = Assert.Throws<PaymentException>(() => DriverNotificationRules.Since(value, Now));
        Assert.Equal(400, error.Status);
        Assert.Equal("INVALID_SINCE", error.Code);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData("999999999999999999999999")]
    public void InvalidCursorHasStructuredCode(string value) =>
        Assert.Equal("INVALID_CURSOR", Assert.Throws<PaymentException>(() => DriverNotificationRules.After(value)).Code);

    [Fact]
    public void RetryScheduleMatchesReceiptLeaseAndBackoff()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), DriverNotificationRules.Lease);
        Assert.Equal(new[] { 30d, 120d, 600d, 1800d }, Enumerable.Range(1, 4).Select(i => DriverNotificationRules.RetryDelay(i).TotalSeconds));
        Assert.Equal(5, DriverNotificationRules.MaxAttempts);
    }
}

public sealed class DriverNotificationApiTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
    private const string Feed = "/payments/driver/notifications";

    [MySqlFact]
    public async Task SavedCardDoubleTapCreatesOneNotificationAndMismatchCreatesNone()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString, clock: new ManualClock(Now));
        using var rider = CheckoutApiTests.RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await CheckoutApiTests.Seed(rider, evt);
        Assert.Equal(0L, await db.Count("driver_payment_notifications"));
        var form = await CheckoutApiTests.Checkout(rider, evt.TripId!);
        var fields = new Dictionary<string, string>(form.Fields) { ["amount"] = "600.00" };
        (await Notify(rider, form with { Fields = fields }, "320027106010")).EnsureSuccessStatusCode();
        Assert.Equal(0L, await db.Count("driver_payment_notifications"));
        using var cardResponse = await rider.PostAsJsonAsync("/payments/cards",
            new { number = "4242424242424242", expMonth = 12, expYear = 2030, cvc = "123", holderName = "Rider One" });
        cardResponse.EnsureSuccessStatusCode();
        var cardId = (await cardResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cardId").GetString();
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => rider.PostAsJsonAsync($"/payments/{evt.TripId}/pay", new { cardId })));
        foreach (var response in responses) { response.EnsureSuccessStatusCode(); response.Dispose(); }
        Assert.Equal(1L, await db.Count("driver_payment_notifications"));
        using var driver = app.CreateClient();
        driver.DefaultRequestHeaders.Add("Cookie", "session=driver-1");
        var notification = Assert.Single((await driver.GetFromJsonAsync<DriverNotificationPage>(Feed))!.Notifications);
        Assert.Equal(725.50m, notification.Amount);
        Assert.Equal("4242", notification.CardLast4);
    }

    [MySqlFact]
    public async Task ConcurrentPaidRedeliveriesAndLateCompletionKeepOneFinalAmountSnapshot()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString, clock: new ManualClock(Now));
        using var rider = CheckoutApiTests.RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await CheckoutApiTests.Seed(rider, evt);
        var corrected = evt with { EventId = Guid.NewGuid().ToString(), OccurredAt = evt.OccurredAt.AddSeconds(1), Payload = new() { FinalFare = 921.37m, EstimatedFare = 600m } };
        (await CheckoutApiTests.PostEvent(rider, corrected)).EnsureSuccessStatusCode();
        var form = await CheckoutApiTests.Checkout(rider, evt.TripId!);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Notify(rider, form, "320027106001")));
        foreach (var response in responses) { response.EnsureSuccessStatusCode(); response.Dispose(); }
        (await Notify(rider, form, "320027106002")).EnsureSuccessStatusCode();
        (await CheckoutApiTests.PostEvent(rider, evt)).EnsureSuccessStatusCode();
        await db.ApplySchemaAsync();
        await db.ApplySchemaAsync();
        Assert.Equal(1L, await db.Count("driver_payment_notifications"));
        using var driver = app.CreateClient();
        driver.DefaultRequestHeaders.Add("Cookie", "session=driver-1");
        using var responseFeed = await driver.GetAsync(Feed);
        Assert.True(responseFeed.Headers.CacheControl?.NoStore);
        var notification = Assert.Single((await responseFeed.Content.ReadFromJsonAsync<DriverNotificationPage>())!.Notifications);
        Assert.Equal(evt.TripId, notification.TripId);
        Assert.Equal(921.37m, notification.Amount);
        Assert.Equal("LKR", notification.Currency);
        Assert.Equal("VISA", notification.CardBrand);
        Assert.Equal("1292", notification.CardLast4);
        Assert.Equal(Now, notification.PaidAt);
        Assert.Equal(notification, await driver.GetFromJsonAsync<DriverNotification>(Feed + "/" + evt.TripId));
        (await driver.GetAsync("/health")).EnsureSuccessStatusCode();

        // The schema itself enforces uniqueness, independent of the verification guard.
        var duplicate = await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync("""
            INSERT INTO driver_payment_notifications (trip_id, payment_id, event_id, driver_id, amount_minor, currency, paid_at, next_attempt_at)
            SELECT trip_id, 'different-payment', 'different-event', driver_id, amount_minor, currency, paid_at, next_attempt_at FROM driver_payment_notifications
            """));
        Assert.Equal(1062, duplicate.Number);
    }

    [MySqlFact]
    public async Task OutboxFailureRollsBackPaidConfirmationAndReceiptThenRedeliverySucceeds()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString, clock: new ManualClock(Now));
        using var rider = CheckoutApiTests.RiderClient(app);
        var evt = PaymentRulesTests.Completion();
        await CheckoutApiTests.Seed(rider, evt);
        var form = await CheckoutApiTests.Checkout(rider, evt.TripId!);
        await db.ExecuteAsync("DROP TABLE driver_payment_notifications");
        await CheckoutApiTests.Error(await Notify(rider, form, "320027106003"), 503, "PAYMENT_STORE_UNAVAILABLE");
        Assert.Equal("Pending", (await rider.GetFromJsonAsync<PaymentRecord>("/payments/" + evt.TripId))!.Status);
        foreach (var table in new[] { "payment_confirmations", "payment_receipts", "payment_verifications" }) Assert.Equal(0L, await db.Count(table));
        await db.ApplySchemaAsync();
        (await Notify(rider, form, "320027106003")).EnsureSuccessStatusCode();
        Assert.Equal(1L, await db.Count("driver_payment_notifications"));
    }

    [MySqlFact]
    public async Task RiderAndOtherUsersCannotReadDriverNotificationAndSinceFilters()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString, clock: new ManualClock(Now));
        using var rider = CheckoutApiTests.RiderClient(app);
        var trip = await Paid(rider);
        foreach (var identity in new[] { "rider-1", "other", "driver-1" })
        {
            using var client = app.CreateClient();
            client.DefaultRequestHeaders.Add("Cookie", "session=" + identity);
            var page = (await client.GetFromJsonAsync<DriverNotificationPage>(Feed))!;
            if (identity == "driver-1") Assert.Single(page.Notifications);
            else
            {
                Assert.Empty(page.Notifications);
                await CheckoutApiTests.Error(await client.GetAsync(Feed + "/" + trip), 404, "NOTIFICATION_NOT_FOUND");
            }
        }
        using var driver = app.CreateClient();
        driver.DefaultRequestHeaders.Add("Cookie", "session=driver-1");
        Assert.Single((await driver.GetFromJsonAsync<DriverNotificationPage>(Feed + "?since=2026-10-11T12:00:00Z"))!.Notifications);
        await db.ExecuteAsync("UPDATE driver_payment_notifications SET paid_at = '2026-09-01 00:00:00'");
        Assert.Empty((await driver.GetFromJsonAsync<DriverNotificationPage>(Feed))!.Notifications);
        Assert.Single((await driver.GetFromJsonAsync<DriverNotificationPage>(Feed + "?since=2026-09-01T00:00:00Z"))!.Notifications);
    }

    [Fact]
    public async Task AuthenticationValidationAndStoreFailureAreStructured()
    {
        await using var app = new PaymentApplication("Server=127.0.0.1;Port=1;User ID=unused;Connection Timeout=1", clock: new ManualClock(Now));
        using var client = app.CreateClient();
        await CheckoutApiTests.Error(await client.GetAsync(Feed), 401, "AUTHENTICATION_REQUIRED");
        await CheckoutApiTests.Error(await client.GetAsync(Feed + "/trip"), 401, "AUTHENTICATION_REQUIRED");
        client.DefaultRequestHeaders.Add("Cookie", "session=driver-1");
        foreach (var query in new[] { "since=", "since=bad", "since=2026-10-12T00:00:00Z" })
            await CheckoutApiTests.Error(await client.GetAsync(Feed + "?" + query), 400, "INVALID_SINCE");
        foreach (var query in new[] { "driverId=other", "since=a&since=b", "after=1&after=2" })
            await CheckoutApiTests.Error(await client.GetAsync(Feed + "?" + query), 400, "INVALID_REQUEST");
        await CheckoutApiTests.Error(await client.GetAsync(Feed + "?after=-1"), 400, "INVALID_CURSOR");
        await CheckoutApiTests.Error(await client.GetAsync(Feed + "/" + new string('x', 129)), 400, "INVALID_REQUEST");
        await CheckoutApiTests.Error(await client.GetAsync(Feed), 503, "PAYMENT_STORE_UNAVAILABLE");
    }

    [MySqlFact]
    public async Task DispatcherWithoutUrlLogsHonestlyAndNeverResends()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString, clock: new ManualClock(Now));
        using var rider = CheckoutApiTests.RiderClient(app);
        await Paid(rider);
        var dispatcher = app.Services.GetRequiredService<DriverNotificationDispatcher>();
        Assert.True(await dispatcher.SendNextAsync(default));
        Assert.False(await dispatcher.SendNextAsync(default));
        var row = await State(db);
        Assert.Equal(("Logged", 1, false), row);
        using var scope = app.Services.CreateScope();
        Assert.Single((await scope.ServiceProvider.GetRequiredService<DriverNotificationStore>().ListAsync("driver-1", Now, 0, default)).Notifications);
    }

    [MySqlFact]
    public async Task ConcurrentClaimsRecoverExpiredLeaseAndRejectStaleWorkerCompletion()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString, clock: new ManualClock(Now));
        using var rider = CheckoutApiTests.RiderClient(app);
        var trip = await Paid(rider);
        using var scope = app.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DriverNotificationStore>();
        var claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.ClaimNextAsync(Now, default)));
        var first = Assert.Single(claims, c => c.HasValue)!.Value;
        Assert.Null(await store.ClaimNextAsync(Now.AddMinutes(1), default));
        var second = (await store.ClaimNextAsync(Now.AddMinutes(3), default))!.Value;
        Assert.Equal(first.Content.EventId, second.Content.EventId);
        Assert.Equal(2, second.Attempts);
        await store.FinishAsync(trip, first.Token, "Accepted", Now, Now, null, default);
        Assert.Equal(("Sending", 2, false), await State(db));
        await store.FinishAsync(trip, second.Token, "Logged", Now, Now, null, default);
        Assert.Equal(("Logged", 2, false), await State(db));
    }

    [MySqlFact]
    public async Task DispatcherRetriesTransientHttpFailuresWithStableEventIdThenAccepts()
    {
        await using var db = await TestDatabase.CreateAsync();
        var clock = new ManualClock(Now);
        var handler = new NotificationHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.Accepted);
        await using var root = new PaymentApplication(db.ConnectionString, clock: clock, settings: HttpSettings());
        await using var app = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient("DriverNotifications").ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var rider = CheckoutApiTests.RiderClient(app);
        var trip = await Paid(rider);
        var dispatcher = app.Services.GetRequiredService<DriverNotificationDispatcher>();
        Assert.True(await dispatcher.SendNextAsync(default));
        Assert.Equal(("Retry", 1, false), await State(db));
        Assert.False(await dispatcher.SendNextAsync(default));
        clock.Advance(DriverNotificationRules.RetryDelay(1));
        Assert.True(await dispatcher.SendNextAsync(default));
        Assert.Equal(("Accepted", 2, true), await State(db));
        Assert.False(await dispatcher.SendNextAsync(default));
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[0]);
        Assert.Equal(trip, body.GetProperty("tripId").GetString());
        Assert.Equal("driver-1", body.GetProperty("driverId").GetString());
        Assert.Equal(725.50m, body.GetProperty("amount").GetDecimal());
        Assert.Equal(body.GetProperty("eventId").GetString(), handler.Keys[0]);
    }

    [MySqlFact]
    public async Task DispatcherStopsAtRetryLimitAndOnPermanentConfigurationFailure()
    {
        await using var db = await TestDatabase.CreateAsync();
        var clock = new ManualClock(Now);
        var handler = new NotificationHandler(HttpStatusCode.TooManyRequests);
        await using var root = new PaymentApplication(db.ConnectionString, clock: clock, settings: HttpSettings());
        await using var app = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient("DriverNotifications").ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var rider = CheckoutApiTests.RiderClient(app);
        await Paid(rider);
        var dispatcher = app.Services.GetRequiredService<DriverNotificationDispatcher>();
        for (var attempt = 1; attempt <= DriverNotificationRules.MaxAttempts; attempt++)
        {
            Assert.True(await dispatcher.SendNextAsync(default));
            Assert.Equal((attempt == DriverNotificationRules.MaxAttempts ? "Failed" : "Retry", attempt, false), await State(db));
            Assert.False(await dispatcher.SendNextAsync(default));
            clock.Advance(DriverNotificationRules.RetryDelay(attempt));
        }
        Assert.False(await dispatcher.SendNextAsync(default));
        Assert.Equal(5, handler.Bodies.Count);
        await db.ExecuteAsync("UPDATE driver_payment_notifications SET status = 'Pending', attempts = 0");
        app.Services.GetRequiredService<IConfiguration>()["Notification:DriverPaymentPath"] = "";
        Assert.True(await dispatcher.SendNextAsync(default));
        Assert.Equal(("Failed", 1, false), await State(db));
        Assert.Equal(5, handler.Bodies.Count);
    }

    [MySqlFact]
    public async Task FeedPagesEveryTripWithEqualPaidTimestamp()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString, clock: new ManualClock(Now));
        using var rider = CheckoutApiTests.RiderClient(app);
        await Paid(rider);
        // Populate a full page using unique trips while preserving the same paid timestamp.
        for (var i = 0; i < DriverNotificationRules.PageSize; i++)
        {
            await db.ExecuteAsync("""
                INSERT INTO payments (trip_id, document) VALUES (@trip, '{}');
                INSERT INTO driver_payment_notifications (trip_id, payment_id, event_id, driver_id, amount_minor, currency, paid_at, next_attempt_at)
                VALUES (@trip, @trip, @trip, 'driver-1', 72550, 'LKR', @now, @now)
                """, ("@trip", "page-" + i), ("@now", Now.UtcDateTime));
        }
        using var driver = app.CreateClient();
        driver.DefaultRequestHeaders.Add("Cookie", "session=driver-1");
        var first = (await driver.GetFromJsonAsync<DriverNotificationPage>(Feed))!;
        Assert.Equal(DriverNotificationRules.PageSize, first.Notifications.Count);
        Assert.NotNull(first.NextCursor);
        var second = (await driver.GetFromJsonAsync<DriverNotificationPage>(Feed + "?after=" + first.NextCursor))!;
        Assert.Single(second.Notifications);
        Assert.Null(second.NextCursor);
        Assert.Equal(101, first.Notifications.Concat(second.Notifications).Select(n => n.TripId).Distinct().Count());
    }

    private static Dictionary<string, string?> HttpSettings() => new()
    {
        ["Notification:BaseUrl"] = "https://notification.test",
        ["Notification:DriverPaymentPath"] = "/api/notifications/driver/card-payment-succeeded"
    };

    private static async Task<string> Paid(HttpClient rider)
    {
        var evt = PaymentRulesTests.Completion();
        await CheckoutApiTests.Seed(rider, evt);
        var form = await CheckoutApiTests.Checkout(rider, evt.TripId!);
        (await Notify(rider, form, "320027106004")).EnsureSuccessStatusCode();
        return evt.TripId!;
    }

    private static Task<HttpResponseMessage> Notify(HttpClient client, CheckoutForm form, string paymentId) =>
        client.PostAsync("/payments/payhere/notify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["merchant_id"] = TestPayHere.MerchantId,
            ["order_id"] = form.OrderId,
            ["payment_id"] = paymentId,
            ["payhere_amount"] = form.Fields["amount"],
            ["payhere_currency"] = "LKR",
            ["status_code"] = "2",
            ["md5sig"] = PayHereSignature.NotifySignature(TestPayHere.MerchantId, form.OrderId, form.Fields["amount"], "LKR", "2", TestPayHere.Secret),
            ["method"] = "VISA",
            ["card_no"] = "************1292"
        }));

    private static async Task<(string Status, int Attempts, bool Accepted)> State(TestDatabase db)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT status, attempts, accepted_at FROM driver_payment_notifications", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt32(1), !reader.IsDBNull(2));
    }
}

internal sealed class NotificationHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
{
    public List<string> Bodies { get; } = [];
    public List<string> Keys { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        Keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
        return new(statuses[Math.Min(Bodies.Count - 1, statuses.Length - 1)]);
    }
}

public sealed class DriverNotificationSenderTests
{
    private static readonly DriverNotificationContent Content = new("event", "payment", "driver",
        new("trip", 725.50m, "LKR", "VISA", "1292", DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(400, true)]
    [InlineData(401, true)]
    [InlineData(404, true)]
    [InlineData(408, false)]
    [InlineData(429, false)]
    [InlineData(503, false)]
    public async Task HttpFailuresClassifyRetryWithoutPersistingProviderResponse(int status, bool permanent)
    {
        var handler = new NotificationHandler((HttpStatusCode)status);
        var sender = Sender(handler);
        var error = await Assert.ThrowsAsync<DriverNotificationDeliveryException>(() => sender.SendAsync(Content, default));
        Assert.Equal(permanent, error.Permanent);
        Assert.Equal("NOTIFICATION_HTTP_" + status, error.Message);
    }

    [Theory]
    [InlineData(false, "NOTIFICATION_UNAVAILABLE")]
    [InlineData(true, "NOTIFICATION_TIMEOUT")]
    public async Task NetworkFailuresRetryWithSafeErrorCode(bool timeout, string code)
    {
        var sender = Sender(new ThrowingHandler(timeout));
        var error = await Assert.ThrowsAsync<DriverNotificationDeliveryException>(() => sender.SendAsync(Content, default));
        Assert.Equal(code, error.Message);
        Assert.False(error.Permanent);
    }

    [Fact]
    public async Task ShutdownCancellationPropagatesForLeaseRecovery()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sender(new ThrowingHandler(true)).SendAsync(Content, cancellation.Token));
    }

    private static DriverNotificationSender Sender(HttpMessageHandler handler) => new(new ClientFactory(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notification:BaseUrl"] = "https://notification.test",
            ["Notification:DriverPaymentPath"] = "/driver/payment"
        }).Build(), NullLogger<DriverNotificationSender>.Instance);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class ThrowingHandler(bool timeout) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw (timeout ? new TaskCanceledException("Transport details") : new HttpRequestException("Transport details"));
    }
}
