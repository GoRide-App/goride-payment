using GoRide.Payment.Models;
using GoRide.Payment.Receipts;
using Xunit;

namespace GoRide.Payment.Tests;

// SCRUM-681: whitebox tests for the receipt email content.
public sealed class ReceiptRendererTests
{
    private static ReceiptContent Content(string? name = "Rider One", FareBreakdown? breakdown = null) => new(
        "4b1f2c9e-0000-4000-8000-000000000001", "trip-42", TestRider.Email, name,
        123450, "LKR", "VISA", "1292", "320027150123",
        new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero), breakdown);

    [Fact]
    public void ReceiptShowsTheVerifiedAmountCardReferencesAndSriLankaTime()
    {
        var email = ReceiptRenderer.Render(Content());
        Assert.Equal(TestRider.Email, email.To);
        Assert.Equal("Rider One", email.ToName);
        Assert.Equal("Your GoRide receipt · LKR 1,234.50", email.Subject);
        Assert.Equal("4b1f2c9e-0000-4000-8000-000000000001", email.ReferenceId);
        foreach (var line in new[] { "Hi Rider,", "Total paid: LKR 1,234.50", "Paid with: VISA ending 1292",
            "PayHere reference: 320027150123", "Trip reference: trip-42", "Receipt number: 4b1f2c9e-0000-4000-8000-000000000001" })
            Assert.Contains(line, email.Text);
        // 10:00 UTC is 3:30 in the afternoon in Colombo.
        Assert.Contains("Paid on: 9 Oct 2026, 3:30", email.Text);
        Assert.Contains("(Sri Lanka time)", email.Html);
        Assert.Contains("LKR 1,234.50", email.Html);
    }

    [Fact]
    public void BreakdownListsOnlyChargedParts()
    {
        var email = ReceiptRenderer.Render(Content(breakdown: new FareBreakdown(500m, 600m, 134.50m, 0m, 0m, 1234.50m)));
        Assert.Contains("Base fare: LKR 500.00", email.Text);
        Assert.Contains("Distance: LKR 600.00", email.Text);
        Assert.Contains("Time: LKR 134.50", email.Text);
        Assert.DoesNotContain("Stops:", email.Text);
        Assert.DoesNotContain("Waiting:", email.Text);
        var withExtras = ReceiptRenderer.Render(Content(breakdown: new FareBreakdown(500m, 600m, 34.50m, 50m, 50m, 1234.50m)));
        Assert.Contains("Stops: LKR 50.00", withExtras.Text);
        Assert.Contains("Waiting: LKR 50.00", withExtras.Text);
    }

    [Fact]
    public void RiderSuppliedTextIsHtmlEncoded()
    {
        var email = ReceiptRenderer.Render(Content(name: "<script>alert(1)</script> Doe"));
        Assert.DoesNotContain("<script>", email.Html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", email.Html);
    }

    [Fact]
    public void MissingNameAndCardDetailsFallBackToNeutralText()
    {
        var email = ReceiptRenderer.Render(Content(name: null) with { CardBrand = null, CardLast4 = null });
        Assert.StartsWith("Hi there,", email.Text);
        Assert.Contains("Paid with: Card", email.Text);
        Assert.DoesNotContain("<", email.Text);
    }

    [Theory]
    [InlineData(0.5, "LKR 0.50")]
    [InlineData(725.5, "LKR 725.50")]
    [InlineData(1234567.8, "LKR 1,234,567.80")]
    public void AmountsUseTwoDecimalsAndThousandsSeparators(double amount, string expected) =>
        Assert.Equal(expected, ReceiptRenderer.Format((decimal)amount, "LKR"));

    [Fact]
    public void SriLankaTimeIsFiveThirtyAhead() =>
        Assert.Equal(TimeSpan.FromMinutes(330),
            ReceiptRenderer.ToSriLankaTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).Offset);
}
