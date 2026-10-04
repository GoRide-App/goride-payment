using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GoRide.Payment.Data;
using GoRide.Payment.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Xunit;

namespace GoRide.Payment.Tests;

public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAYMENT_TEST_MYSQL")))
            Skip = "Set PAYMENT_TEST_MYSQL to a disposable MySQL connection; CI always supplies it.";
    }
}

public sealed class PaymentApiTests
{
    [MySqlFact]
    public async Task CompletedTripCanSelectCardThroughHttpAndRetryAcrossRestart()
    {
        await using var db = await TestDatabase.CreateAsync();
        var evt = PaymentRulesTests.Completion();
        string id;
        await using (var api = new PaymentApplication(db.ConnectionString))
        using (var client = api.CreateClient())
        {
            var completed = await Complete(client, evt);
            id = completed.Id;
            client.DefaultRequestHeaders.Add("Cookie", "session=rider-1");
            var selected = await Select(client, evt.TripId!);
            Assert.Equal(id, selected.Id);
            Assert.Equal("Card", selected.Method);
            Assert.Equal(725.50m, selected.FinalFare);
            Assert.Equal("Pending", selected.Status);
            Assert.Null(selected.ProcessedAt);
            Assert.Equal(0, selected.CardAttemptCount);
        }
        await using var restarted = new PaymentApplication(db.ConnectionString);
        using var retry = restarted.CreateClient();
        retry.DefaultRequestHeaders.Add("Cookie", "session=rider-1");
        Assert.Equal(id, (await Complete(retry, evt)).Id);
        Assert.Equal("Card", (await Select(retry, evt.TripId!)).Method);
        var saved = await retry.GetFromJsonAsync<PaymentRecord>($"/payments/{evt.TripId}");
        Assert.Equal(id, saved!.Id);
        Assert.Equal(1L, await db.Count("payments"));
        Assert.Equal(1L, await db.Count("processed_payment_events"));
    }

    [MySqlFact]
    public async Task ParallelDeliveriesAndSelectionsProduceOnePayment()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var api = new PaymentApplication(db.ConnectionString);
        using var client = api.CreateClient();
        var evt = PaymentRulesTests.Completion();
        var payments = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Complete(client, evt)));
        Assert.Single(payments.Select(p => p.Id).Distinct());
        client.DefaultRequestHeaders.Add("Cookie", "session=rider-1");
        var selected = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Select(client, evt.TripId!)));
        Assert.All(selected, p => { Assert.Equal("Card", p.Method); Assert.Equal(0, p.CardAttemptCount); });
        Assert.Equal(1L, await db.Count("payments"));
        Assert.Equal(1L, await db.Count("processed_payment_events"));
    }

    [MySqlFact]
    public async Task NewEventIdsForSameTripKeepOnePaymentAndExactDecimalFare()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var api = new PaymentApplication(db.ConnectionString);
        using var client = api.CreateClient();
        var evt = PaymentRulesTests.Completion() with { Payload = new() { FinalFare = 99999999.99m, EstimatedFare = 123.29m } };
        var payments = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Complete(client, evt with { EventId = $"delivery-{i}" })));
        Assert.Single(payments.Select(p => p.Id).Distinct());
        client.DefaultRequestHeaders.Add("Cookie", "session=rider-1");
        var body = await client.GetFromJsonAsync<JsonElement>($"/payments/{evt.TripId}");
        Assert.Equal(JsonValueKind.Number, body.GetProperty("finalFare").ValueKind);
        Assert.Equal(99999999.99m, body.GetProperty("finalFare").GetDecimal());
        Assert.Equal(123.29m, body.GetProperty("estimatedFare").GetDecimal());
        Assert.Equal(1L, await db.Count("payments"));
        Assert.Equal(8L, await db.Count("processed_payment_events"));
    }

    [MySqlFact]
    public async Task MissingCompletionReturnsJsonNullAndExpiredSessionIsRejected()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var api = new PaymentApplication(db.ConnectionString);
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "session=expired");
        await AssertError(await client.PostAsJsonAsync("/payments/trip/select-method", new { method = "Card" }), 401, "AUTHENTICATION_REQUIRED");
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", "session=rider-1");
        using var response = await client.GetAsync("/payments/not-completed");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("null", await response.Content.ReadAsStringAsync());
    }

    [MySqlFact]
    public async Task FareCorrectionsSerializeWithSelectionAndIgnoreOldEvents()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var api = new PaymentApplication(db.ConnectionString);
        using var client = api.CreateClient();
        var evt = PaymentRulesTests.Completion();
        await Complete(client, evt);
        client.DefaultRequestHeaders.Add("Cookie", "session=rider-1");
        var correction = evt with { EventId = "correction", OccurredAt = evt.OccurredAt.AddSeconds(1), Payload = new() { FinalFare = 950.75m } };
        await Task.WhenAll(Complete(client, correction), Select(client, evt.TripId!));
        await Complete(client, evt);
        await Complete(client, evt with { EventId = "old-redelivery" });
        var saved = await client.GetFromJsonAsync<PaymentRecord>($"/payments/{evt.TripId}");
        Assert.Equal(950.75m, saved!.FinalFare);
        Assert.Equal("Card", saved.Method);
        Assert.Equal(3L, await db.Count("processed_payment_events"));
    }

    [MySqlFact]
    public async Task EventIdReuseWithDifferentDataRollsBackAllWrites()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var api = new PaymentApplication(db.ConnectionString);
        using var client = api.CreateClient();
        var evt = PaymentRulesTests.Completion();
        await Complete(client, evt);
        await AssertError(await PostEvent(client, evt with { Payload = new() { FinalFare = 1 } }), 409, "EVENT_ID_CONFLICT");
        await AssertError(await PostEvent(client, evt with { TripId = "different-trip" }), 409, "EVENT_ID_CONFLICT");
        await AssertError(await PostEvent(client, evt with { EventId = "bad-owner", RiderId = "other" }), 409, "TRIP_IDENTITY_CONFLICT");
        Assert.Equal(1L, await db.Count("payments"));
        Assert.Equal(1L, await db.Count("processed_payment_events"));
    }

    [MySqlFact]
    public async Task AuthenticationOwnershipAndCompletionAreRequired()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var api = new PaymentApplication(db.ConnectionString);
        using var client = api.CreateClient();
        var evt = PaymentRulesTests.Completion();
        await Complete(client, evt);
        await AssertError(await client.PostAsJsonAsync($"/payments/{evt.TripId}/select-method", new { method = "Card" }), 401, "AUTHENTICATION_REQUIRED");
        client.DefaultRequestHeaders.Add("Cookie", "session=other");
        await AssertError(await client.PostAsJsonAsync($"/payments/{evt.TripId}/select-method", new { method = "Card" }), 403, "PAYMENT_FORBIDDEN");
        await AssertError(await client.GetAsync($"/payments/{evt.TripId}"), 403, "PAYMENT_FORBIDDEN");
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", "session=rider-1");
        await AssertError(await client.PostAsJsonAsync("/payments/in-progress-trip/select-method", new { method = "Card" }), 409, "TRIP_NOT_COMPLETED");
        Assert.Null((await client.GetFromJsonAsync<PaymentRecord>($"/payments/{evt.TripId}"))!.Method);
    }

    [MySqlFact]
    public async Task ValidationRejectsUnknownMethodMalformedJsonAndClientAmounts()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var api = new PaymentApplication(db.ConnectionString);
        using var client = api.CreateClient();
        var evt = PaymentRulesTests.Completion();
        await Complete(client, evt);
        client.DefaultRequestHeaders.Add("Cookie", "session=rider-1");
        foreach (var method in new string?[] { null, "Cash", "card", "", "crypto" })
            await AssertError(await client.PostAsJsonAsync($"/payments/{evt.TripId}/select-method", new { method }), 400, "INVALID_PAYMENT_METHOD");
        foreach (var body in new[] { "{", "null", "{\"method\":\"Card\",\"finalFare\":1}", "{\"method\":\"Card\",\"riderId\":\"other\"}" })
            await AssertError(await client.PostAsync($"/payments/{evt.TripId}/select-method", new StringContent(body, Encoding.UTF8, "application/json")), 400, "INVALID_REQUEST");
        await AssertError(await PostEvent(client, evt with { EventId = "missing-fare", Payload = new() }), 400, "INVALID_FARE");
        await AssertError(await PostEvent(client, evt with { EventId = "started", EventType = "TRIP_STARTED" }), 400, "INVALID_EVENT");
        Assert.Equal(1L, await db.Count("processed_payment_events"));
    }

    [MySqlFact]
    public async Task InternalEndpointRejectsBrowserAndWrongServiceKey()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var api = new PaymentApplication(db.ConnectionString);
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "session=rider-1");
        await AssertError(await client.PostAsJsonAsync("/internal/trip-events", PaymentRulesTests.Completion()), 401, "INVALID_SERVICE_KEY");
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "wrong");
        await AssertError(await client.PostAsJsonAsync("/internal/trip-events", PaymentRulesTests.Completion()), 401, "INVALID_SERVICE_KEY");
        Assert.Equal(0L, await db.Count("payments"));
    }

    [Fact]
    public async Task IdentityFailureIsUnavailableAndNeverAllowsSelection()
    {
        await using var api = new PaymentApplication("Server=127.0.0.1;Port=1;User ID=unused;Connection Timeout=1");
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "session=unavailable");
        await AssertError(await client.PostAsJsonAsync("/payments/trip/select-method", new { method = "Card" }), 503, "IDENTITY_UNAVAILABLE");
    }

    [Fact]
    public async Task DatabaseFailureReturnsStructuredErrorWithoutConnectionDetails()
    {
        await using var api = new PaymentApplication("Server=127.0.0.1;Port=1;User ID=unused;Password=do-not-leak;Connection Timeout=1");
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "session=rider-1");
        var response = await client.PostAsJsonAsync("/payments/trip/select-method", new { method = "Card" });
        Assert.DoesNotContain("do-not-leak", await response.Content.ReadAsStringAsync());
        await AssertError(response, 503, "PAYMENT_STORE_UNAVAILABLE");
    }

    private static async Task<HttpResponseMessage> PostEvent(HttpClient client, TripCompletedEvent evt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/trip-events") { Content = JsonContent.Create(evt) };
        request.Headers.Add("X-Internal-Api-Key", "test-service-key");
        return await client.SendAsync(request);
    }

    private static async Task<PaymentRecord> Complete(HttpClient client, TripCompletedEvent evt)
    {
        using var response = await PostEvent(client, evt);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PaymentRecord>())!;
    }

    private static async Task<PaymentRecord> Select(HttpClient client, string tripId)
    {
        using var response = await client.PostAsJsonAsync($"/payments/{tripId}/select-method", new { method = "Card" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PaymentRecord>())!;
    }

    private static async Task AssertError(HttpResponseMessage response, int status, string code)
    {
        using (response)
        {
            Assert.Equal(status, (int)response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(code, body.GetProperty("code").GetString());
        }
    }
}

internal sealed class PaymentApplication(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Payments"] = connectionString,
            ["InternalServices:ApiKey"] = "test-service-key",
            ["Kafka:Enabled"] = "false",
            ["Identity:BaseUrl"] = "http://identity.test/"
        }));
        builder.ConfigureServices(services => services.AddHttpClient("Identity")
            .ConfigurePrimaryHttpMessageHandler(() => new IdentityStub()));
    }
}

internal sealed class IdentityStub : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var cookie = request.Headers.GetValues("Cookie").Single();
        if (cookie == "session=unavailable") throw new HttpRequestException("Identity offline");
        var user = cookie switch { "session=rider-1" => "rider-1", "session=other" => "other", _ => null };
        return Task.FromResult(user is null ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { userId = user }) });
    }
}

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string adminConnection;
    private readonly string name = "payment_test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; }

    private TestDatabase(string admin)
    {
        adminConnection = new MySqlConnectionStringBuilder(admin) { Database = "" }.ConnectionString;
        ConnectionString = new MySqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    public static async Task<TestDatabase> CreateAsync()
    {
        var db = new TestDatabase(Environment.GetEnvironmentVariable("PAYMENT_TEST_MYSQL")!);
        await using var admin = new MySqlConnection(db.adminConnection);
        await admin.OpenAsync();
        await using (var create = new MySqlCommand($"CREATE DATABASE `{db.name}`", admin)) await create.ExecuteNonQueryAsync();
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var schema = new MySqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql")), connection);
        await schema.ExecuteNonQueryAsync();
        return db;
    }

    public async Task<long> Count(string table)
    {
        if (table is not ("payments" or "processed_payment_events")) throw new ArgumentException("Unknown table.");
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand($"SELECT COUNT(*) FROM {table}", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async ValueTask DisposeAsync()
    {
        MySqlConnection.ClearAllPools();
        await using var admin = new MySqlConnection(adminConnection);
        await admin.OpenAsync();
        // Only the generated database owned by this fixture is removed.
        await using var command = new MySqlCommand($"DROP DATABASE `{name}`", admin);
        await command.ExecuteNonQueryAsync();
    }
}
