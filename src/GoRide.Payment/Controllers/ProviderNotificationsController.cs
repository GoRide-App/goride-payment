using GoRide.Payment.Services;
using GoRide.Payment.Verification;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GoRide.Payment.Controllers;

// PayHere's server calls this; there is no rider session. Trust comes only from the
// verified md5sig, never from the caller's address or any browser state.
// No [ApiController]: nothing is model-bound, and reading the form here keeps every
// failure (size, encoding, fields) on this endpoint's own structured error codes.
[AllowAnonymous]
[Route("payments/payhere/notify")]
public sealed class ProviderNotificationsController(PaymentVerificationService verification) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Notify(CancellationToken ct)
    {
        if (!Request.HasFormContentType)
            throw new PaymentException(415, "UNSUPPORTED_MEDIA_TYPE", "PayHere notifications must be form encoded.");
        IFormCollection form;
        try { form = await Request.ReadFormAsync(ct); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new PaymentException(413, "NOTIFICATION_TOO_LARGE", "The notification body is too large.");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or BadHttpRequestException)
        {
            throw new PaymentException(400, "INVALID_NOTIFICATION", "The notification body could not be read.");
        }
        var result = await verification.VerifyAsync(PayHereNotice.From(form), ct);
        // 200 acknowledges receipt so PayHere stops retrying. Notices that need
        // reconciliation are recorded and flagged here rather than rejected.
        return Ok(new
        {
            outcome = result.Outcome,
            tripId = result.Payment.TripId,
            paymentStatus = result.Payment.Status,
            duplicate = result.Duplicate,
            reconciliationRequired = result.Outcome is VerificationOutcome.AmountMismatch
                or VerificationOutcome.DuplicatePayment or VerificationOutcome.Chargedback
        });
    }
}
