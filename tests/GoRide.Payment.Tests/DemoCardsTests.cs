using GoRide.Payment.Cards;
using GoRide.Payment.Services;
using Xunit;

namespace GoRide.Payment.Tests;

// Whitebox tests for demo card validation and the Stripe-like test card outcomes.
public sealed class DemoCardsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TestCardIsNormalisedAndOnlyItsLastFourDigitsAreKept()
    {
        var card = DemoCards.Validate("4242 4242-4242 4242", 12, 30, "123", "  Rider One  ", Now);
        Assert.Equal("Visa", card.Brand);
        Assert.Equal("4242", card.Last4);
        Assert.Equal(2030, card.ExpYear);
        Assert.Equal("Rider One", card.HolderName);
        Assert.Equal(CardBehaviour.Succeeds, card.Behaviour);
        Assert.Matches("^[0-9a-f]{64}$", card.Fingerprint);
        Assert.DoesNotContain("4242424242424242", card.ToString());
    }

    [Theory]
    [InlineData("4242", "CARD_NUMBER_INVALID")]
    [InlineData("4242 4242 4242 4241", "CARD_NUMBER_INVALID")]
    [InlineData("4242 4242 4242 424x", "CARD_NUMBER_INVALID")]
    [InlineData(null, "CARD_NUMBER_INVALID")]
    [InlineData("4111 1111 1111 1111", "CARD_NOT_TEST_CARD")]
    public void WrongCardNumbersAreRejected(string? number, string code) =>
        AssertInvalid(() => DemoCards.Validate(number, 12, 2030, "123", null, Now), code);

    [Theory]
    [InlineData(13, 2030, "CARD_EXPIRY_INVALID")]
    [InlineData(0, 2030, "CARD_EXPIRY_INVALID")]
    [InlineData(null, 2030, "CARD_EXPIRY_INVALID")]
    [InlineData(9, 2026, "CARD_EXPIRED")]
    [InlineData(12, 2025, "CARD_EXPIRED")]
    [InlineData(12, 2047, "CARD_EXPIRY_INVALID")]
    public void WrongExpiryDatesAreRejected(int? month, int? year, string code) =>
        AssertInvalid(() => DemoCards.Validate("4242424242424242", month, year, "123", null, Now), code);

    [Fact]
    public void CardExpiringThisMonthIsStillValid() =>
        Assert.Equal(2026, DemoCards.Validate("4242424242424242", 10, 26, "123", null, Now).ExpYear);

    [Theory]
    [InlineData(null)]
    [InlineData("12")]
    [InlineData("1234")]
    [InlineData("12a")]
    public void WrongSecurityCodesAreRejected(string? cvc) =>
        AssertInvalid(() => DemoCards.Validate("4242424242424242", 12, 2030, cvc, null, Now), "CARD_CVC_INVALID");

    [Fact]
    public void OverlongOrControlCharacterNamesAreRejected()
    {
        AssertInvalid(() => DemoCards.Validate("4242424242424242", 12, 2030, "123", new string('a', 101), Now), "CARD_NAME_INVALID");
        AssertInvalid(() => DemoCards.Validate("4242424242424242", 12, 2030, "123", "Rider\nOne", Now), "CARD_NAME_INVALID");
    }

    [Theory]
    [InlineData("4000000000000002", "CARD_DECLINED")]
    [InlineData("4000000000009995", "INSUFFICIENT_FUNDS")]
    [InlineData("4000000000000069", "EXPIRED_CARD")]
    [InlineData("4000000000000127", "INCORRECT_CVC")]
    [InlineData("4000000000000119", "PROCESSING_ERROR")]
    public void DecliningTestCardsSaveButDeclineWhenCharged(string number, string code)
    {
        var card = DemoCards.Validate(number, 12, 2030, "123", null, Now);
        var decline = DemoCards.Decline(card.Behaviour);
        Assert.Equal(code, decline?.Code);
        Assert.Equal(402, decline?.Status);
    }

    [Fact]
    public void SucceedingTestCardsDoNotDecline()
    {
        foreach (var test in DemoCards.TestCards.Where(card => card.Behaviour == CardBehaviour.Succeeds))
            Assert.Null(DemoCards.Decline(DemoCards.Validate(test.Number, 12, 2030, "123", null, Now).Behaviour));
    }

    [Fact]
    public void EveryPublishedTestCardPassesLuhn() =>
        Assert.All(DemoCards.TestCards, card => Assert.True(DemoCards.PassesLuhn(card.Number), card.Number));

    [Theory]
    [InlineData(9, 2026, true)]
    [InlineData(10, 2026, false)]
    [InlineData(1, 2027, false)]
    public void ExpiryIsJudgedInSriLankaTime(int month, int year, bool expired) =>
        Assert.Equal(expired, DemoCards.IsExpired(month, year, Now));

    private static void AssertInvalid(Action validate, string code)
    {
        var error = Assert.Throws<PaymentException>(validate);
        Assert.Equal(400, error.Status);
        Assert.Equal(code, error.Code);
    }
}
