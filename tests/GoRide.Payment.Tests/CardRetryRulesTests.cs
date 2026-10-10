using GoRide.Payment.Cards;
using GoRide.Payment.Services;
using Xunit;

namespace GoRide.Payment.Tests;

public sealed class CardRetryRulesTests
{
    [Theory]
    [InlineData("processing_error", true)]
    [InlineData("PROVIDER_TIMEOUT", true)]
    [InlineData("PROVIDER_UNAVAILABLE", true)]
    [InlineData("card_declined", false)]
    [InlineData("insufficient_funds", false)]
    [InlineData("expired_card", false)]
    [InlineData("incorrect_cvc", false)]
    [InlineData("CARD_NUMBER_INVALID", false)]
    [InlineData("UNKNOWN", false)]
    public void OnlyKnownTransientFailuresGetExactlyOneRetry(string code, bool transient)
    {
        Assert.Equal(transient, CardRetryRules.IsTransient(code));
        Assert.False(CardRetryRules.ShouldRetry(code, 0));
        Assert.Equal(transient, CardRetryRules.ShouldRetry(code, 1));
        Assert.False(CardRetryRules.ShouldRetry(code, 2));
        Assert.False(CardRetryRules.ShouldRetry(code, 3));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData(" a09b4f00-3c7d-4a77-bb45-f02031359def ")]
    public void RequestIdentityIsRequired(string? id) =>
        Assert.Equal("INVALID_REQUEST", Assert.Throws<PaymentException>(() => CardRetryRules.RequireRequestId(id)).Code);

    [Fact]
    public void LifetimeAttemptsDoNotDisableManualCardRetries()
    {
        var payment = PaymentRules.ApplyCompletion(null, PaymentRulesTests.Completion()) with { CardAttemptCount = 7 };
        var selected = PaymentRules.SelectCard(payment, "rider-1");
        Assert.False(selected.CardDisabled);
        Assert.Equal(7, selected.CardAttemptCount);
    }

    [Fact]
    public void RecoveryCardDeterministicallySucceedsOnlyOnItsRetry()
    {
        var card = DemoCards.Validate("4000000000000341", 12, 2030, "123", null, DateTimeOffset.Parse("2026-10-11T00:00:00Z"));
        Assert.Equal("PROCESSING_ERROR", DemoCards.Decline(card.Behaviour, 1)?.Code);
        Assert.Null(DemoCards.Decline(card.Behaviour, 2));
    }
}
