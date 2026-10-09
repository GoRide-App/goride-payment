using GoRide.Payment.Checkout;
using Xunit;

namespace GoRide.Payment.Tests;

// Reference values were computed independently with Python's hashlib using PayHere's documented formulas.
public sealed class PayHereSignatureTests
{
    private const string Order = "goride-00000000000000000000000000000001";

    [Fact]
    public void CheckoutHashMatchesPayHereFormula() =>
        Assert.Equal("A57AC583190B79FBEE86C5B90CAF9967",
            PayHereSignature.CheckoutHash("1211149", Order, 72550, "LKR", TestPayHere.Secret));

    [Fact]
    public void NotifySignatureMatchesPayHereFormula() =>
        Assert.Equal("27DB7A3FDFE143F82263A909FC9FB431",
            PayHereSignature.NotifySignature("1211149", Order, "725.50", "LKR", "2", TestPayHere.Secret));

    [Theory]
    [InlineData(72550, "725.50")]
    [InlineData(100000, "1000.00")]
    [InlineData(5, "0.05")]
    public void AmountsUseTwoDecimalsWithoutGrouping(long minor, string expected) =>
        Assert.Equal(expected, PayHereSignature.FormatAmount(minor));

    [Fact]
    public void SignatureComparisonIsCaseInsensitiveAndRejectsOtherValues()
    {
        const string expected = "27DB7A3FDFE143F82263A909FC9FB431";
        Assert.True(PayHereSignature.Matches(expected, expected.ToLowerInvariant()));
        Assert.False(PayHereSignature.Matches(expected, null));
        Assert.False(PayHereSignature.Matches(expected, expected[..^1]));
        Assert.False(PayHereSignature.Matches(expected, "A57AC583190B79FBEE86C5B90CAF9967"));
    }
}
