using System.Net;

namespace GoRide.Payment.DriverNotifications;

public interface IDriverNotificationSender
{
    Task<string> SendAsync(DriverNotificationContent content, CancellationToken ct);
}

public sealed class DriverNotificationDeliveryException(string message, bool permanent = false) : Exception(message)
{
    public bool Permanent { get; } = permanent;
}

// The existing notification service has rider payment and driver booking-change endpoints,
// but no idempotent driver payment receiver. Enable HTTP only once that contract is available.
public sealed class DriverNotificationSender(IHttpClientFactory clients, IConfiguration configuration,
    ILogger<DriverNotificationSender> logger) : IDriverNotificationSender
{
    public async Task<string> SendAsync(DriverNotificationContent content, CancellationToken ct)
    {
        var baseUrl = configuration["Notification:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogInformation("Driver payment notification {EventId} logged; notification delivery is not configured.", content.EventId);
            return "Logged";
        }
        var path = configuration["Notification:DriverPaymentPath"];
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(baseUri.UserInfo)
            || string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.StartsWith("//")
            || path.Contains('\\') || !Uri.TryCreate(baseUri, path, out var uri)
            || uri.Authority != baseUri.Authority)
            throw new DriverNotificationDeliveryException("NOTIFICATION_CONFIGURATION_INVALID", permanent: true);

        var notification = content.Notification;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new
            {
                content.EventId,
                eventType = "DRIVER_CARD_PAYMENT_SUCCEEDED",
                content.PaymentId,
                content.DriverId,
                notification.TripId,
                notification.Amount,
                notification.Currency,
                notification.PaidAt
            })
        };
        request.Headers.Add("Idempotency-Key", content.EventId);
        if (!string.IsNullOrWhiteSpace(configuration["Notification:ApiKey"]))
            request.Headers.Add("X-Internal-Api-Key", configuration["Notification:ApiKey"]);
        try
        {
            using var response = await clients.CreateClient("DriverNotifications").SendAsync(request, ct);
            // Accepted by the service does not prove FCM delivered it to a device.
            if (response.IsSuccessStatusCode) return "Accepted";
            var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500;
            throw new DriverNotificationDeliveryException($"NOTIFICATION_HTTP_{(int)response.StatusCode}", !transient);
        }
        catch (HttpRequestException) { throw new DriverNotificationDeliveryException("NOTIFICATION_UNAVAILABLE"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DriverNotificationDeliveryException("NOTIFICATION_TIMEOUT");
        }
    }
}
