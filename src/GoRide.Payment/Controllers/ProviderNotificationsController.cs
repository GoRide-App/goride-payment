using GoRide.Payment.Verification;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GoRide.Payment.Controllers;

// PayHere's server calls this; there is no rider session. Trust comes only from the
// verified md5sig, never from the caller's address or any browser state.
[ApiController]
[AllowAnonymous]
[Route("payments/payhere/notify")]
public sealed class ProviderNotificationsController(PaymentVerificationService verification) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Notify(CancellationToken ct)
    {
        var form = await Request.ReadFormAsync(ct);
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
