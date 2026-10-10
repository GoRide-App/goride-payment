using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GoRide.Payment.Receipts;

public sealed record EmailMessage(string To, string? ToName, string Subject, string Html, string Text, string ReferenceId);

// Transient failures are retried with backoff; permanent ones (bad address, rejected by
// the provider) fail the receipt straight away.
public sealed class EmailDeliveryException(string message, bool permanent) : Exception(message)
{
    public bool Permanent { get; } = permanent;
}

public interface IEmailSender
{
    string Name { get; }
    Task<string> SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed class EmailSettings(IConfiguration configuration)
{
    public string Provider => configuration["Email:Provider"] ?? "Log";
    public string FromAddress => configuration["Email:FromAddress"] ?? "";
    public string FromName => configuration["Email:FromName"] ?? "GoRide";
    public string BrevoApiKey => configuration["Email:Brevo:ApiKey"] ?? "";
    public bool UseBrevo => string.Equals(Provider, "Brevo", StringComparison.OrdinalIgnoreCase);
}

// Brevo transactional email (free plan: 300 emails/day). The sender address must be a
// verified sender in the Brevo account.
public sealed class BrevoEmailSender(IHttpClientFactory clients, EmailSettings settings) : IEmailSender
{
    public string Name => "Brevo";

    public async Task<string> SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.BrevoApiKey) || !ReceiptRules.IsDeliverableEmail(settings.FromAddress))
            throw new EmailDeliveryException("Brevo is not configured: set Email:Brevo:ApiKey and a verified Email:FromAddress.", false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
        request.Headers.Add("api-key", settings.BrevoApiKey);
        request.Content = JsonContent.Create(new
        {
            sender = new { name = settings.FromName, email = settings.FromAddress },
            to = new[] { new { email = message.To, name = message.ToName ?? message.To } },
            subject = message.Subject,
            htmlContent = message.Html,
            textContent = message.Text,
            tags = new[] { "goride-receipt" },
            headers = new Dictionary<string, string> { ["X-GoRide-Receipt"] = message.ReferenceId }
        });
        try
        {
            using var response = await clients.CreateClient("Brevo").SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                return json.RootElement.TryGetProperty("messageId", out var id) ? id.GetString() ?? "brevo" : "brevo";
            }
            // 401/403 mean the key or this server's IP is not allowed (an account setting), so the
            // receipt is retried until that is fixed. Other 4xx (except throttling) reject this
            // message and will not succeed on retry. Provider error bodies are not kept.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new EmailDeliveryException(
                    $"Brevo refused the API key or this server's IP ({(int)response.StatusCode}). Check the key and Brevo's authorised IPs.", false);
            var permanent = (int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.TooManyRequests;
            throw new EmailDeliveryException($"Brevo rejected the email ({(int)response.StatusCode}).", permanent);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new EmailDeliveryException("Brevo could not be reached.", false);
        }
    }
}

// Local development: no email leaves the machine. The rendered receipt is logged and kept
// in memory so the development page can show exactly what would have been sent.
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    private static readonly ConcurrentDictionary<string, EmailMessage> Sent = new();
    public string Name => "Log";

    public Task<string> SendAsync(EmailMessage message, CancellationToken ct)
    {
        // Keeps only recent messages so a long-running local server does not grow without bound.
        if (Sent.Count >= 200)
            foreach (var key in Sent.Keys.Take(Sent.Count - 199)) Sent.TryRemove(key, out _);
        Sent[message.ReferenceId] = message;
        logger.LogInformation("Receipt email (log provider) to {Recipient}: {Subject}", ReceiptRules.MaskEmail(message.To), message.Subject);
        return Task.FromResult("log-" + Guid.NewGuid().ToString("N"));
    }

    public static EmailMessage? Find(string referenceId) => Sent.TryGetValue(referenceId, out var message) ? message : null;
}
