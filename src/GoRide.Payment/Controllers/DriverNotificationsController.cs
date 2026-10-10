using System.Security.Claims;
using GoRide.Payment.DriverNotifications;
using GoRide.Payment.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GoRide.Payment.Controllers;

[ApiController]
[Authorize]
[Route("payments/driver/notifications")]
public sealed class DriverNotificationsController(DriverNotificationStore store, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (Request.Query.Any(pair => pair.Key is not ("since" or "after") || pair.Value.Count != 1))
            throw new PaymentException(400, "INVALID_REQUEST", "Only one since timestamp and one after cursor are allowed.");
        var since = DriverNotificationRules.Since(Request.Query.ContainsKey("since") ? Request.Query["since"].ToString() : null, clock.GetUtcNow());
        var after = DriverNotificationRules.After(Request.Query.ContainsKey("after") ? Request.Query["after"].ToString() : null);
        return Ok(await store.ListAsync(User.FindFirstValue("sub")!, since, after, ct));
    }

    [HttpGet("{tripId}")]
    public async Task<IActionResult> Get(string tripId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        PaymentRules.ValidateId(tripId, "tripId");
        return Ok(await store.GetAsync(User.FindFirstValue("sub")!, tripId, ct)
            ?? throw new PaymentException(404, "NOTIFICATION_NOT_FOUND", "The driver payment notification was not found."));
    }
}
