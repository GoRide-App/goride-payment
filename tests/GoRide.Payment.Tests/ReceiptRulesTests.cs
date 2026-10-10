using GoRide.Payment.Receipts;
using Xunit;

namespace GoRide.Payment.Tests;

// SCRUM-681: whitebox tests for the pure receipt rules.
public sealed class ReceiptRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("rider@goride.lk")]
    [InlineData("first.last+trip@mail.example.com")]
    public void DeliverableEmailsAreAccepted(string email) => Assert.True(ReceiptRules.IsDeliverableEmail(email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" rider@goride.lk")]
    [InlineData("rider@goride.lk ")]
    [InlineData("rider@localhost")]
    [InlineData("Rider One <rider@goride.lk>")]
    [InlineData("rider@goride.lk\r\nBcc: someone@example.com")]
    [InlineData("rider@@goride.lk")]
    [InlineData("rider@goride.")]
    [InlineData("not-an-email")]
    public void UndeliverableOrInjectedEmailsAreRejected(string? email) => Assert.False(ReceiptRules.IsDeliverableEmail(email));

    [Fact]
    public void EmailsLongerThanTheSmtpLimitAreRejected() =>
        Assert.False(ReceiptRules.IsDeliverableEmail(new string('a', 245) + "@goride.lk"));

    [Theory]
    [InlineData("shageeshan@gmail.com", "s***n@gmail.com")]
    [InlineData("ab@goride.lk", "a***@goride.lk")]
    [InlineData("a@goride.lk", "a***@goride.lk")]
    [InlineData("no-at-sign", "***")]
    [InlineData(null, null)]
    public void MaskedEmailKeepsOnlyFirstAndLastLetterAndDomain(string? email, string? masked) =>
        Assert.Equal(masked, ReceiptRules.MaskEmail(email));

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 600)]
    [InlineData(4, 1800)]
    [InlineData(9, 1800)]
    public void RetryDelayBacksOffAndCaps(int attempts, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ReceiptRules.RetryDelay(attempts));

    [Fact]
    public void ErrorSummaryIsCappedToTheColumnSize()
    {
        Assert.Equal("short", ReceiptRules.ErrorSummary("short"));
        var summary = ReceiptRules.ErrorSummary(new string('x', 500));
        Assert.Equal(300, summary.Length);
        Assert.EndsWith("...", summary);
    }

    [Fact]
    public void SentReceiptCanBeResentAfterTheCooldown()
    {
        var sent = Row(ReceiptStatus.Sent, sentAt: Now);
        Assert.Equal(Now + ReceiptRules.ResendCooldown, ReceiptService.ResendAvailableAt(sent));
        var tooSoon = ReceiptService.ResendBlocker(sent, Now.AddSeconds(20));
        Assert.Equal("RECEIPT_RESEND_TOO_SOON", tooSoon?.Code);
        Assert.Equal(429, tooSoon?.Status);
        Assert.Equal(TimeSpan.FromSeconds(40), tooSoon?.RetryAfter);
        Assert.Null(ReceiptService.ResendBlocker(sent, Now + ReceiptRules.ResendCooldown));
    }

    [Fact]
    public void CooldownRunsFromTheLaterOfSendAndResendRequest()
    {
        var requestedLater = Row(ReceiptStatus.Sent, sentAt: Now, requestedAt: Now.AddMinutes(5));
        Assert.Equal(Now.AddMinutes(5) + ReceiptRules.ResendCooldown, ReceiptService.ResendAvailableAt(requestedLater));
        var sentLater = Row(ReceiptStatus.Sent, sentAt: Now.AddMinutes(5), requestedAt: Now);
        Assert.Equal(Now.AddMinutes(5) + ReceiptRules.ResendCooldown, ReceiptService.ResendAvailableAt(sentLater));
        Assert.Null(ReceiptService.ResendAvailableAt(Row(ReceiptStatus.Failed)));
    }

    [Theory]
    [InlineData(ReceiptStatus.Pending, "RECEIPT_IN_PROGRESS", 409)]
    [InlineData(ReceiptStatus.Sending, "RECEIPT_IN_PROGRESS", 409)]
    [InlineData(ReceiptStatus.Retry, "RECEIPT_IN_PROGRESS", 409)]
    [InlineData(ReceiptStatus.NoEmail, "RECEIPT_EMAIL_MISSING", 409)]
    public void ResendIsRefusedUntilTheReceiptHasFinished(string status, string code, int httpStatus)
    {
        var row = Row(status) with { Recipient = status == ReceiptStatus.NoEmail ? null : TestRider.Email };
        var blocked = ReceiptService.ResendBlocker(row, Now);
        Assert.Equal(code, blocked?.Code);
        Assert.Equal(httpStatus, blocked?.Status);
    }

    [Fact]
    public void ResendIsLimitedAndAFailedReceiptCanBeRetriedAtOnce()
    {
        Assert.Null(ReceiptService.ResendBlocker(Row(ReceiptStatus.Failed), Now));
        var limited = ReceiptService.ResendBlocker(Row(ReceiptStatus.Sent, resends: ReceiptRules.MaxResends), Now.AddDays(1));
        Assert.Equal("RECEIPT_RESEND_LIMIT", limited?.Code);
        Assert.Equal(429, limited?.Status);
        Assert.Null(limited?.RetryAfter);
    }

    private static ReceiptRow Row(string status, DateTimeOffset? sentAt = null, DateTimeOffset? requestedAt = null, int resends = 0) =>
        new("trip-1", Guid.NewGuid().ToString(), TestRider.Email, status, 1, resends, sentAt, requestedAt, "Log", null);
}
