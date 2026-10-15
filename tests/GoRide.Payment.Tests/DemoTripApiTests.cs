using System.Net;
using System.Net.Http.Json;
using GoRide.Payment.Checkout;
using GoRide.Payment.Models;
using GoRide.Payment.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GoRide.Payment.Tests;

public sealed class DemoTripApiTests
{
    [MySqlFact]
    public async Task DisabledByDefaultAndExplicitlyOffInEveryEnvironment()
    {
        await using var db = await TestDatabase.CreateAsync();
        foreach (var environment in new[] { "Production", "Development" })
        foreach (var enabled in new string?[] { null, "false" })
        {
            await using var app = new PaymentApplication(db.ConnectionString, environment,
                new() { ["DemoTrips:Enabled"] = enabled });
            using var client = Client(app);
            using var response = await Complete(client, "demo_trp_disabled");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.Equal(0L, await db.Count("payments"));
    }

    [MySqlFact]
    public async Task OnlyNamespacedIdsAreAcceptedIncludingUuidAndBase36Generators()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = Enabled(db);
        using var client = Client(app);
        foreach (var trip in new[] { null, "", "trp_live", "trip-123", "demo_other", "demo_trp_",
            "demo_trp_x/y", "demo_trp_x.y", "demo_trp_é", "demo_trp_x\n", " demo_trp_x", "demo_trp_" + new string('a', 101) })
            await CheckoutApiTests.Error(await Complete(client, trip), 400, "INVALID_REQUEST");
        foreach (var trip in new[] { "demo_trp_" + Guid.NewGuid(), "demo_trp_q8ax31mgt9y4nz", "demo_trp_A-0_z",
            "demo_trp_a", "demo_trp_" + new string('a', 100) })
        {
            using var response = await Complete(client, trip);
            response.EnsureSuccessStatusCode();
            Assert.Equal(trip, (await response.Content.ReadFromJsonAsync<PaymentRecord>())!.TripId);
        }
        Assert.Equal(5L, await db.Count("payments"));
    }

    [MySqlFact]
    public async Task FareMustBePositiveWithAtMostTwoDecimalsAndWithinDefaultMaximum()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = Enabled(db);
        using var client = Client(app);
        foreach (var fare in new decimal?[] { null, 0, -1, 0.001m, 1.001m, 100000.01m })
            await CheckoutApiTests.Error(await Complete(client, "demo_trp_invalidfare", fare), 400, "INVALID_FARE");
        foreach (var fare in new[] { 0.01m, 12.34m, 100000m })
        {
            using var response = await Complete(client, "demo_trp_" + Guid.NewGuid(), fare);
            response.EnsureSuccessStatusCode();
            Assert.Equal(fare, (await response.Content.ReadFromJsonAsync<PaymentRecord>())!.FinalFare);
        }
        Assert.Equal(3L, await db.Count("payments"));
    }

    [MySqlFact]
    public async Task ConfiguredMaximumIsEnforcedInDevelopmentToo()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new PaymentApplication(db.ConnectionString, "Development",
            new() { ["DemoTrips:Enabled"] = "true", ["DemoTrips:MaxFare"] = "42.75" });
        using var client = Client(app);
        await CheckoutApiTests.Error(await Complete(client, "demo_trp_cap", 42.76m), 400, "INVALID_FARE");
        using var response = await Complete(client, "demo_trp_cap", 42.75m);
        response.EnsureSuccessStatusCode();
        Assert.Equal(42.75m, (await response.Content.ReadFromJsonAsync<PaymentRecord>())!.FinalFare);
    }

    [MySqlFact]
    public async Task RiderComesOnlyFromTheAuthenticatedSession()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = Enabled(db);
        using var anonymous = app.CreateClient();
        await CheckoutApiTests.Error(await Complete(anonymous, "demo_trp_identity"), 401, "AUTHENTICATION_REQUIRED");
        using var client = Client(app);
        await CheckoutApiTests.Error(await client.PostAsJsonAsync("/payments/demo-completions",
            new { tripId = "demo_trp_identity", finalFare = 640m, riderId = "other" }), 400, "INVALID_REQUEST");
        using var response = await Complete(client, "demo_trp_identity");
        response.EnsureSuccessStatusCode();
        Assert.Equal("rider-1", (await response.Content.ReadFromJsonAsync<PaymentRecord>())!.RiderId);
    }

    [MySqlFact]
    public async Task RepeatReturnsTheSameRecordAndConflictsNeverChangeIt()
    {
        await using var db = await TestDatabase.CreateAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var app = new PaymentApplication(db.ConnectionString, "Production",
            new() { ["DemoTrips:Enabled"] = "true" }, clock);
        using var client = Client(app);
        using var first = await Complete(client, "demo_trp_repeat");
        first.EnsureSuccessStatusCode();
        var original = await first.Content.ReadFromJsonAsync<PaymentRecord>();
        clock.Advance(TimeSpan.FromMinutes(5));
        using var repeat = await Complete(client, "demo_trp_repeat");
        repeat.EnsureSuccessStatusCode();
        Assert.Equal(original, await repeat.Content.ReadFromJsonAsync<PaymentRecord>());
        using var other = Client(app, "other");
        await CheckoutApiTests.Error(await Complete(other, "demo_trp_repeat"), 409, "DEMO_TRIP_CONFLICT");
        await CheckoutApiTests.Error(await Complete(client, "demo_trp_repeat", 640.01m), 409, "DEMO_TRIP_CONFLICT");
        Assert.Equal(original, await client.GetFromJsonAsync<PaymentRecord>("/payments/demo_trp_repeat"));
        Assert.Equal(1L, await db.Count("payments"));
        Assert.Equal(1L, await db.Count("processed_payment_events"));
    }

    [MySqlFact]
    public async Task ConcurrentCompletionsAreIdempotentAndCannotReplaceFareOrRider()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = Enabled(db);
        using var client = Client(app);
        using var other = Client(app, "other");
        var repeats = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Complete(client, "demo_trp_concurrent")));
        var records = new List<PaymentRecord?>();
        foreach (var response in repeats)
        using (response)
        {
            response.EnsureSuccessStatusCode();
            records.Add(await response.Content.ReadFromJsonAsync<PaymentRecord>());
        }
        Assert.All(records, record => Assert.Equal(records[0], record));
        foreach (var changeRider in new[] { false, true })
        {
            var trip = "demo_trp_" + Guid.NewGuid();
            var responses = await Task.WhenAll(Complete(client, trip), Complete(changeRider ? other : client, trip, changeRider ? 640m : 641m));
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            foreach (var response in responses)
            {
                if (response.StatusCode == HttpStatusCode.Conflict)
                    await CheckoutApiTests.Error(response, 409, "DEMO_TRIP_CONFLICT");
                else response.Dispose();
            }
        }
        Assert.Equal(3L, await db.Count("payments"));
        Assert.Equal(3L, await db.Count("processed_payment_events"));
    }

    [MySqlFact]
    public async Task InternalHttpAndTheSharedKafkaEntryPointRejectTheEntireDemoNamespace()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = Enabled(db);
        using var client = Client(app);
        using var created = await Complete(client, "demo_trp_reserved");
        created.EnsureSuccessStatusCode();
        var original = await created.Content.ReadFromJsonAsync<PaymentRecord>();
        using var scope = app.Services.CreateScope();
        var completions = scope.ServiceProvider.GetRequiredService<TripCompletionService>();
        foreach (var trip in new[] { "demo_trp_reserved", "demo_trp_uncreated", "demo_anything", "demo_" })
        {
            var evt = PaymentRulesTests.Completion(trip);
            await CheckoutApiTests.Error(await CheckoutApiTests.PostEvent(client, evt), 400, "INVALID_REQUEST");
            var error = await Assert.ThrowsAsync<PaymentException>(() => completions.CompleteAsync(evt, CancellationToken.None));
            Assert.Equal(400, error.Status);
            Assert.Equal("INVALID_REQUEST", error.Code);
        }
        Assert.Equal(original, await client.GetFromJsonAsync<PaymentRecord>("/payments/demo_trp_reserved"));
        await CheckoutApiTests.Seed(client, PaymentRulesTests.Completion("trp_live"));
        await CheckoutApiTests.Error(await Complete(client, "trp_live"), 400, "INVALID_REQUEST");
        Assert.Equal(2L, await db.Count("payments"));
        Assert.Equal(2L, await db.Count("processed_payment_events"));
    }

    private static PaymentApplication Enabled(TestDatabase db) => new(db.ConnectionString, "Production",
        new() { ["DemoTrips:Enabled"] = "true" });

    private static HttpClient Client(PaymentApplication app, string session = "rider-1")
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "session=" + session);
        return client;
    }

    private static Task<HttpResponseMessage> Complete(HttpClient client, string? tripId, decimal? finalFare = 640m) =>
        client.PostAsJsonAsync("/payments/demo-completions", new { tripId, finalFare });
}
