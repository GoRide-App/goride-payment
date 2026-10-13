using System.Net;
using System.Text;
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
        Assert.Contains("details@example.com", error.Message);
        Assert.DoesNotContain("test-key", error.Message);
        Assert.DoesNotContain(Message.ReferenceId, error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task IpBlocksIncludeTheProviderReason(HttpStatusCode status)
    {
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() => Sender(new StubHandler(status,
            """{"code":"unauthorized","message":"We have detected you are using an unrecognised IP address 2400:ff00::1; If you performed this action make sure to add the new IP address in this link: https://app.brevo.com/security/authorised_ips"}""")).SendAsync(Message, default));
        Assert.StartsWith($"Brevo refused the API key or this server's IP ({(int)status}): ", error.Message);
        Assert.Contains("unrecognised IP address 2400:ff00::1", error.Message);
        Assert.False(error.Permanent);
    }

    [Fact]
    public async Task MissingKeysIncludeTheProviderReason()
    {
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() => Sender(new StubHandler(HttpStatusCode.Unauthorized,
            """{"code":"unauthorized","message":"Key not found"}""")).SendAsync(Message, default));
        Assert.Equal("Brevo refused the API key or this server's IP (401): Key not found", error.Message);
        Assert.False(error.Permanent);
    }

    [Theory]
    [InlineData("  Service\r\n\t unavailable\0\u001b\u200b\u202e  ", "Service unavailable")]
    [InlineData("{\"message\":\"  Service\\r\\n\\t unavailable\\u0000\\u200b  \"}", "Service unavailable")]
    [InlineData("{\"message\":\"Clé introuvable\"}", "Clé introuvable")]
    [InlineData("{\"code\":\"error\"}", "{\"code\":\"error\"}")]
    [InlineData("{\"message\":null}", "{\"message\":null}")]
    [InlineData("[]", "[]")]
    [InlineData("{broken json", "{broken json")]
    public async Task ErrorReasonsArePrintableAndFallBackToRawText(string body, string reason)
    {
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            Sender(new StubHandler(HttpStatusCode.BadRequest, body)).SendAsync(Message, default));
        Assert.Equal("Brevo rejected the email (400): " + reason, error.Message);
        Assert.True(error.Permanent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task LongReasonsFitTheStoredErrorWithoutFurtherTruncation(HttpStatusCode status)
    {
        var body = JsonSerializer.Serialize(new { message = new string('x', 1000) });
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            Sender(new StubHandler(status, body)).SendAsync(Message, default));
        Assert.Equal(290, error.Message.Length);
        Assert.EndsWith("...", error.Message);
        Assert.Equal(error.Message, ReceiptRules.ErrorSummary(error.Message));
    }

    [Fact]
    public async Task HugeBodiesAreReadOnlyUpToTwoKilobytes()
    {
        using var stream = new CountingStream(Encoding.UTF8.GetBytes(new string('x', 50000)));
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            Sender(new StubHandler(HttpStatusCode.BadRequest, "", new StreamContent(stream))).SendAsync(Message, default));
        Assert.Equal(2048, stream.BytesRead);
        Assert.Equal(290, error.Message.Length);
        Assert.EndsWith("...", error.Message);
        Assert.True(error.Permanent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, false)]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task UnreadableBodiesKeepTheRetryClassification(HttpStatusCode status, bool permanent)
    {
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            Sender(new StubHandler(status, "", new StreamContent(new UnreadableStream()))).SendAsync(Message, default));
        Assert.Equal(permanent, error.Permanent);
        if (status == HttpStatusCode.Created) Assert.Equal("Brevo could not be reached.", error.Message);
        else Assert.Contains($"({(int)status}).", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Brevo refused the API key or this server's IP (401). Check the key and Brevo's authorised IPs.")]
    [InlineData(HttpStatusCode.Forbidden, "Brevo refused the API key or this server's IP (403). Check the key and Brevo's authorised IPs.")]
    [InlineData(HttpStatusCode.BadRequest, "Brevo rejected the email (400).")]
    [InlineData(HttpStatusCode.TooManyRequests, "Brevo rejected the email (429).")]
    [InlineData(HttpStatusCode.InternalServerError, "Brevo rejected the email (500).")]
    public async Task EmptyBodiesKeepTheExistingMessage(HttpStatusCode status, string expected)
    {
        foreach (var body in new[] { "", " \r\n\t\0\u200b", "{\"message\":\"\"}" })
        {
            var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
                Sender(new StubHandler(status, body)).SendAsync(Message, default));
            Assert.Equal(expected, error.Message);
        }
    }

    [Theory]
    [InlineData("Rejected test-key")]
    [InlineData("{\"message\":\"Rejected test\\u002dkey\"}")]
    public async Task EchoedApiKeysAreRedacted(string body)
    {
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            Sender(new StubHandler(HttpStatusCode.Unauthorized, body)).SendAsync(Message, default));
        Assert.Contains("Rejected [redacted]", error.Message);
        Assert.DoesNotContain("test-key", error.Message);
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
    private sealed class StubHandler(HttpStatusCode? status, string body, HttpContent? content = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (status is null) throw new HttpRequestException("No such host is known.");
            return new HttpResponseMessage(status.Value) { Content = content ?? new StringContent(body) };
        }
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var count = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 64)], ct);
            BytesRead += count;
            return count;
        }
    }

    private sealed class UnreadableStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            throw new IOException("Response body interrupted.");
    }
}
