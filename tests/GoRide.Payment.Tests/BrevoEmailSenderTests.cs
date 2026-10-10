using System.Net;
using System.Text.Json;
using GoRide.Payment.Receipts;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GoRide.Payment.Tests;

// SCRUM-681: whitebox tests for the Brevo request and how its responses are classified.
public sealed class BrevoEmailSenderTests
{
    private static readonly EmailMessage Message = new(TestRider.Email, "Rider One", "Your GoRide receipt · LKR 725.50",
        "<p>Receipt</p>", "Receipt", "4b1f2c9e-0000-4000-8000-000000000001");

    [Fact]
    public async Task SendsTheReceiptFromTheVerifiedSenderAndReturnsTheMessageId()
    {
        var handler = new StubHandler(HttpStatusCode.Created, """{"messageId":"<202610091000.123@smtp-relay.mailin.fr>"}""");
        var id = await Sender(handler).SendAsync(Message, default);
        Assert.Equal("<202610091000.123@smtp-relay.mailin.fr>", id);
        Assert.Equal("https://api.brevo.com/v3/smtp/email", handler.Request!.RequestUri!.ToString());
        Assert.Equal("test-key", handler.Request.Headers.GetValues("api-key").Single());
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("receipts@goride.test", body.RootElement.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal(TestRider.Email, body.RootElement.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal(Message.Subject, body.RootElement.GetProperty("subject").GetString());
        Assert.Equal("Receipt", body.RootElement.GetProperty("textContent").GetString());
        Assert.Equal(Message.ReferenceId, body.RootElement.GetProperty("headers").GetProperty("X-GoRide-Receipt").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    public async Task OnlyMessageSpecificRejectionsArePermanent(HttpStatusCode status, bool permanent)
    {
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            Sender(new StubHandler(status, """{"code":"error","message":"details@example.com"}""")).SendAsync(Message, default));
        Assert.Equal(permanent, error.Permanent);
        // The provider's error body may contain addresses; it is never copied into the stored error.
        Assert.DoesNotContain("details@example.com", error.Message);
    }

    [Fact]
    public async Task SuccessWithoutAMessageIdDoesNotClaimDelivery()
    {
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            Sender(new StubHandler(HttpStatusCode.Created, "{}")).SendAsync(Message, default));
        Assert.False(error.Permanent);
    }

    [Fact]
    public async Task NetworkFailuresAndMissingConfigurationAreRetried()
    {
        var offline = await Assert.ThrowsAsync<EmailDeliveryException>(() => Sender(new StubHandler(null, "")).SendAsync(Message, default));
        Assert.False(offline.Permanent);
        var unconfigured = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            Sender(new StubHandler(HttpStatusCode.Created, "{}"), apiKey: "").SendAsync(Message, default));
        Assert.False(unconfigured.Permanent);
    }

    private static BrevoEmailSender Sender(StubHandler handler, string apiKey = "test-key") =>
        new(new StubFactory(handler), new EmailSettings(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "Brevo",
            ["Email:FromAddress"] = "receipts@goride.test",
            ["Email:Brevo:ApiKey"] = apiKey
        }).Build()));

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // Returns a fixed response, or throws like an unreachable host when status is null.
    private sealed class StubHandler(HttpStatusCode? status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (status is null) throw new HttpRequestException("No such host is known.");
            return new HttpResponseMessage(status.Value) { Content = new StringContent(body) };
        }
    }
}
