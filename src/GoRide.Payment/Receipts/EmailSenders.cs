using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
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
    public string Provider => configuration["Email:Provider"] ?? "Brevo";
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
            var client = clients.CreateClient("Brevo");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(client.Timeout);
            // Stream the response so an error body is never buffered in full. Keep the
            // client's timeout active while reading the body as well as the headers.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                if (json.RootElement.TryGetProperty("messageId", out var id)
                    && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                    return id.GetString()!;
                throw new EmailDeliveryException("Brevo did not confirm accepting the receipt.", false);
            }
            // 401/403 mean the key or this server's IP is not allowed (an account setting), so the
            // receipt is retried until that is fixed. Other 4xx (except throttling) reject this
            // message and will not succeed on retry.
            var refused = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            var prefix = refused ? $"Brevo refused the API key or this server's IP ({(int)response.StatusCode})"
                : $"Brevo rejected the email ({(int)response.StatusCode})";
            var reason = "";
            try { reason = await ReadErrorReasonAsync(response.Content, settings.BrevoApiKey, timeout.Token); }
            catch (Exception ex) when (ex is IOException or HttpRequestException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                // An unreadable body must not change the status code's retry classification.
            }
            var error = reason.Length == 0
                ? prefix + (refused ? ". Check the key and Brevo's authorised IPs." : ".")
                : prefix + ": " + reason;
            if (error.Length > 290)
            {
                var length = char.IsHighSurrogate(error[286]) ? 286 : 287;
                error = error[..length] + "...";
            }
            var permanent = !refused && (int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.TooManyRequests;
            throw new EmailDeliveryException(error, permanent);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new EmailDeliveryException("Brevo could not be reached.", false);
        }
    }

    private static async Task<string> ReadErrorReasonAsync(HttpContent content, string apiKey, CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[2048];
        var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: ct);
        var reason = Encoding.UTF8.GetString(buffer, 0, count);
        try
        {
            using var json = JsonDocument.Parse(reason);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                reason = message.GetString()!;
        }
        catch (JsonException) { } // Plain text or a JSON body cut off at the read limit.

        var printable = new StringBuilder();
        foreach (var rune in reason.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (printable.Length > 0 && printable[^1] != ' ') printable.Append(' ');
            }
            else if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned))
                printable.Append(rune.ToString());
        }
        // Only response text is retained; redact the configured key even if the provider echoes it.
        return printable.ToString().Trim().Replace(apiKey, "[redacted]", StringComparison.Ordinal);
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
