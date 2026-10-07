using GoRide.Payment.Models;
using GoRide.Payment.Services;
using Xunit;

namespace GoRide.Payment.Tests;

public sealed class PaymentRulesTests
{
    public static TripCompletedEvent Completion(string? tripId = null) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = "TRIP_COMPLETED",
        TripId = tripId ?? Guid.NewGuid().ToString(),
        RiderId = "rider-1",
        DriverId = "driver-1",
        OccurredAt = DateTimeOffset.UtcNow,
        Payload = new CompletedFare { Fare = 600m, FinalFare = 725.50m, EstimatedFare = 600m }
    };

    [Fact]
    public void SelectCardPreservesAuthoritativeFareAndDoesNotCharge()
    {
        var payment = PaymentRules.ApplyCompletion(null, Completion());
        var selected = PaymentRules.SelectCard(payment, "rider-1");
        Assert.Equal("Card", selected.Method);
        Assert.Equal(725.50m, selected.FinalFare);
        Assert.Equal(600m, selected.EstimatedFare);
        Assert.Equal("Pending", selected.Status);
        Assert.Equal(0, selected.CardAttemptCount);
        Assert.Null(selected.ProcessedAt);
        Assert.Equal(selected, PaymentRules.SelectCard(selected, "rider-1"));
    }

    [Fact]
    public void SelectCashPreservesAuthoritativeFareAndDoesNotCharge()
    {
        var payment = PaymentRules.ApplyCompletion(null, Completion());
        var selected = PaymentRules.SelectCash(payment, "rider-1");
        Assert.Equal("Cash", selected.Method);
        Assert.Equal(725.50m, selected.FinalFare);
        Assert.Equal(600m, selected.EstimatedFare);
        Assert.Equal("AwaitingCash", selected.Status);
        Assert.Equal(0, selected.CardAttemptCount);
        Assert.Null(selected.ProcessedAt);
        Assert.Equal(409, Assert.Throws<PaymentException>(() => PaymentRules.SelectCash(selected, "rider-1")).Status);
    }

    [Fact]
    public void DifferentRiderCannotSelectCardOrCash()
    {
        var payment = PaymentRules.ApplyCompletion(null, Completion());
        Assert.Equal(403, Assert.Throws<PaymentException>(() => PaymentRules.SelectCard(payment, "other")).Status);
        Assert.Equal(403, Assert.Throws<PaymentException>(() => PaymentRules.SelectCash(payment, "other")).Status);
    }

    [Theory]
    [InlineData("Charged")]
    [InlineData("Paid")]
    [InlineData("AwaitingCash")]
    [InlineData("Failed")]
    public void NonPendingPaymentsCannotSelectCardOrCash(string status)
    {
        var payment = PaymentRules.ApplyCompletion(null, Completion()) with { Status = status };
        Assert.Equal(409, Assert.Throws<PaymentException>(() => PaymentRules.SelectCard(payment, "rider-1")).Status);
        Assert.Equal(409, Assert.Throws<PaymentException>(() => PaymentRules.SelectCash(payment, "rider-1")).Status);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 2)]
    public void DisabledCardIsRejected(bool disabled, int attempts)
    {
        var payment = PaymentRules.ApplyCompletion(null, Completion()) with { CardDisabled = disabled, CardAttemptCount = attempts };
        Assert.Equal("CARD_DISABLED", Assert.Throws<PaymentException>(() => PaymentRules.SelectCard(payment, "rider-1")).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" trip ")]
    [InlineData("trip\n1")]
    public void InvalidIdsAreRejected(string? id) =>
        Assert.Throws<PaymentException>(() => PaymentRules.ValidateId(id, "tripId"));

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.001")]
    [InlineData("100000000")]
    public void InvalidFinalFareNeverFallsBackToAnEstimate(string input)
    {
        var evt = Completion() with { Payload = new CompletedFare { FinalFare = decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture), Fare = 600 } };
        Assert.Equal("INVALID_FARE", Assert.Throws<PaymentException>(() => PaymentRules.ApplyCompletion(null, evt)).Code);
    }

    [Fact]
    public void MissingFareAndIncompleteTripsAreRejected()
    {
        Assert.Throws<PaymentException>(() => PaymentRules.ApplyCompletion(null, Completion() with { Payload = null }));
        Assert.Throws<PaymentException>(() => PaymentRules.ApplyCompletion(null, Completion() with { Payload = new() }));
        Assert.Throws<PaymentException>(() => PaymentRules.ApplyCompletion(null, Completion() with { EventType = "TRIP_STARTED" }));
        Assert.Throws<PaymentException>(() => PaymentRules.ApplyCompletion(null, Completion() with { OccurredAt = default }));
    }

    [Fact]
    public void ExistingCompletionEnvelopeFareIsSupported()
    {
        var payment = PaymentRules.ApplyCompletion(null, Completion() with { Payload = new() { Fare = 550.25m } });
        Assert.Equal(550.25m, payment.FinalFare);
    }

    [Fact]
    public void RedeliveryAndOlderEventsCannotUndoNewFareOrSelection()
    {
        var evt = Completion();
        var payment = PaymentRules.SelectCard(PaymentRules.ApplyCompletion(null, evt), "rider-1");
        var correction = evt with { EventId = "correction", OccurredAt = evt.OccurredAt.AddSeconds(1), Payload = new() { FinalFare = 800 } };
        var corrected = PaymentRules.ApplyCompletion(payment, correction);
        Assert.Equal(800m, corrected.FinalFare);
        Assert.Equal("Card", corrected.Method);
        Assert.Equal(payment.Id, corrected.Id);
        Assert.Equal(corrected, PaymentRules.ApplyCompletion(corrected, evt));
        Assert.Equal(corrected, PaymentRules.ApplyCompletion(corrected, correction));
    }

    [Fact]
    public void ConflictingParticipantsAndFareVersionsAreRejected()
    {
        var evt = Completion();
        var payment = PaymentRules.ApplyCompletion(null, evt);
        Assert.Throws<PaymentException>(() => PaymentRules.ApplyCompletion(payment, evt with { RiderId = "other" }));
        Assert.Throws<PaymentException>(() => PaymentRules.ApplyCompletion(payment, evt with { DriverId = "other" }));
        Assert.Throws<PaymentException>(() => PaymentRules.ApplyCompletion(payment, evt with { Payload = new() { Fare = 999 } }));
    }

    [Theory]
    [InlineData("Charged")]
    [InlineData("Paid")]
    public void SettledFareCannotBeRewritten(string status)
    {
        var evt = Completion();
        var paid = PaymentRules.ApplyCompletion(null, evt) with { Status = status };
        Assert.Equal(paid, PaymentRules.ApplyCompletion(paid, evt));
        Assert.Equal("PAYMENT_SETTLED", Assert.Throws<PaymentException>(() => PaymentRules.ApplyCompletion(paid,
            evt with { OccurredAt = evt.OccurredAt.AddMinutes(1), Payload = new() { FinalFare = 800 } })).Code);
    }

    [Fact]
    public void BreakdownMustMatchFinalFareAndIsClearedWhenFareChanges()
    {
        var evt = Completion() with { Payload = new() { FinalFare = 100, Breakdown = new(50, 20, 10, 10, 10, 100) } };
        var payment = PaymentRules.ApplyCompletion(null, evt);
        Assert.NotNull(payment.Breakdown);
        Assert.Null(PaymentRules.ApplyCompletion(payment,
            evt with { OccurredAt = evt.OccurredAt.AddSeconds(1), Payload = new() { FinalFare = 120 } }).Breakdown);
        Assert.Throws<PaymentException>(() => PaymentRules.ApplyCompletion(null,
            evt with { Payload = new() { FinalFare = 101, Breakdown = evt.Payload.Breakdown } }));
    }
}
