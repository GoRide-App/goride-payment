"use strict";
const byId = (id) => document.getElementById(id);
const query = new URLSearchParams(window.location.search);
let tripId = query.get("tripId");
let busy = false;
const money = new Intl.NumberFormat("en-LK", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

async function api(path, body) {
  const response = await fetch(`/dev/payments${path}`, {
    method: body === undefined ? "GET" : "POST",
    headers: body === undefined ? {} : { "Content-Type": "application/json", "X-GoRide-Dev": "1" },
    body: body === undefined ? undefined : JSON.stringify(body),
    cache: "no-store",
  });
  const data = await response.json().catch(() => null);
  if (!response.ok) {
    const error = new Error(data?.title || "The request could not be completed. Please retry.");
    error.code = data?.code;
    throw error;
  }
  return data;
}
function feedback(message, error = false) {
  byId("feedback").textContent = message;
  byId("feedback").classList.toggle("error", error);
}
function setBusy(value) {
  busy = value;
  byId("create").disabled = value;
  byId("checkout").disabled = value || !tripId;
  byId("simulate").disabled = value || !tripId;
}
function showTrip(trip) {
  tripId = trip.tripId;
  byId("trip-id").textContent = trip.tripId;
  byId("total").textContent = money.format(trip.finalFare);
  byId("method").textContent = trip.method;
  byId("status").textContent = trip.status;
  byId("trip-state").textContent = "RIDE COMPLETED";
  const url = new URL(window.location.href);
  url.searchParams.set("tripId", tripId);
  window.history.replaceState(null, "", url);
  setBusy(false);
}
byId("trip-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (busy) return;
  setBusy(true);
  feedback("Preparing your test ride…");
  try {
    const email = byId("email").value.trim();
    const trip = await api("/trips", { finalFare: Number(byId("fare").value), email: email || undefined });
    byId("email-receipt").hidden = true;
    showTrip(trip);
    byId("return-notice").hidden = true;
    const url = new URL(window.location.href);
    url.searchParams.delete("result");
    window.history.replaceState(null, "", url);
    feedback("Test ride ready. Continue to PayHere when you’re ready.");
  } catch (error) { feedback(error.message, true); }
  finally { setBusy(false); }
});
byId("checkout").addEventListener("click", async () => {
  if (busy || !tripId) return;
  setBusy(true);
  feedback("Preparing your secure checkout…");
  try {
    const checkout = await api(`/trips/${encodeURIComponent(tripId)}/checkout`, {});
    if (checkout.actionUrl !== "https://sandbox.payhere.lk/pay/checkout")
      throw new Error("The checkout address could not be verified.");
    // PayHere takes a signed form post; the hash was made on the server.
    const form = document.createElement("form");
    form.method = "POST";
    form.action = checkout.actionUrl;
    for (const [name, value] of Object.entries(checkout.fields)) {
      const input = document.createElement("input");
      input.type = "hidden";
      input.name = name;
      input.value = value;
      form.append(input);
    }
    document.body.append(form);
    feedback("Opening the PayHere sandbox…");
    form.submit();
  } catch (error) { feedback(error.message, true); setBusy(false); }
});
// SCRUM-104: the in-app confirmation, read from the same service the rider app polls.
let confirmationId = null;
function showConfirmation(confirmation) {
  confirmationId = confirmation.confirmationId;
  byId("confirmed-amount").textContent = money.format(confirmation.amount);
  byId("confirmed-card").textContent = [confirmation.cardBrand, confirmation.cardLast4 && `•••• ${confirmation.cardLast4}`].filter(Boolean).join(" ") || "Card";
  byId("confirmed-reference").textContent = confirmation.providerReference;
  byId("confirmed-at").textContent = new Date(confirmation.paidAt).toLocaleString();
  byId("confirmed-ack").textContent = confirmation.acknowledgedAt ? new Date(confirmation.acknowledgedAt).toLocaleString() : "Not yet";
  byId("acknowledge").disabled = Boolean(confirmation.acknowledgedAt);
  byId("confirmation").hidden = false;
}
async function loadConfirmation() {
  if (!tripId) return;
  const view = await api(`/trips/${encodeURIComponent(tripId)}/confirmation`);
  if (view.status === "Confirmed") showConfirmation(view.confirmation);
  else byId("confirmation").hidden = true;
}
byId("acknowledge").addEventListener("click", async () => {
  if (!tripId || !confirmationId) return;
  try { showConfirmation(await api(`/trips/${encodeURIComponent(tripId)}/confirmation/acknowledge`, { confirmationId })); }
  catch (error) { feedback(error.message, true); }
});
// SCRUM-105: the email receipt is sent in the background after the payment is verified.
const receiptStates = { Pending: "QUEUED", Sending: "SENDING", Retry: "RETRYING", Sent: "SENT", Failed: "NOT DELIVERED", NoEmail: "NO EMAIL" };
const settled = ["Sent", "Failed", "NoEmail"];
let receiptTimer = null;
function showReceipt(view) {
  byId("receipt-state").textContent = receiptStates[view.status] || view.status.toUpperCase();
  byId("receipt-to").textContent = view.recipient || "No email on file";
  byId("receipt-delivery").textContent = view.status === "NoEmail" ? "Not sent"
    : view.status === "Sent" ? "Delivered to the email provider"
    : view.status === "Failed" ? `Gave up after ${view.attempts} attempts`
    : view.attempts > 0 ? `Attempt ${view.attempts}, retrying` : "Waiting to send";
  byId("receipt-sent-at").textContent = view.sentAt ? new Date(view.sentAt).toLocaleString() : "—";
  byId("receipt-resends").textContent = String(view.resendsLeft);
  byId("resend").disabled = !view.canResend;
  byId("receipt-preview").href = `/dev/payments/trips/${encodeURIComponent(tripId)}/receipt/preview`;
  byId("receipt-preview").hidden = view.status === "NoEmail";
  const waitUntil = view.resendAvailableAt && new Date(view.resendAvailableAt);
  byId("receipt-note").classList.remove("error");
  byId("receipt-note").textContent = view.status === "NoEmail"
    ? "Enter a receipt email when you create the ride to get a receipt."
    : !view.canResend && settled.includes(view.status) && view.resendsLeft > 0 && waitUntil > new Date()
      ? `You can resend after ${waitUntil.toLocaleTimeString()}.` : "";
  byId("email-receipt").hidden = false;
}
async function loadReceipt() {
  if (!tripId) return null;
  try {
    const view = await api(`/trips/${encodeURIComponent(tripId)}/receipt`);
    showReceipt(view);
    return view;
  } catch (error) {
    if (error.code === "RECEIPT_NOT_AVAILABLE") { byId("email-receipt").hidden = true; return null; }
    throw error;
  }
}
// Polls until the background sender has finished with the receipt.
function watchReceipt(polls = 0) {
  clearTimeout(receiptTimer);
  loadReceipt().then((view) => {
    if (view && !settled.includes(view.status) && polls < 40) receiptTimer = setTimeout(() => watchReceipt(polls + 1), 1500);
    else if (view && view.status === "Sent" && !view.canResend) receiptTimer = setTimeout(() => loadReceipt().catch(() => {}), 61000);
  }).catch((error) => { byId("receipt-note").textContent = error.message; byId("receipt-note").classList.add("error"); });
}
byId("resend").addEventListener("click", async () => {
  if (!tripId) return;
  byId("resend").disabled = true;
  try { showReceipt(await api(`/trips/${encodeURIComponent(tripId)}/receipt/resend`, {})); watchReceipt(); }
  catch (error) {
    byId("receipt-note").textContent = error.message;
    byId("receipt-note").classList.add("error");
    loadReceipt().catch(() => {});
  }
});
byId("simulate").addEventListener("click", async () => {
  if (busy || !tripId) return;
  setBusy(true);
  feedback("Sending the signed PayHere notice…");
  try {
    const result = await api(`/trips/${encodeURIComponent(tripId)}/simulate-notify`, { statusCode: 2 });
    showTrip(result.payment);
    await loadConfirmation();
    watchReceipt();
    feedback(result.outcome === "Paid" || result.outcome === "AlreadyPaid"
      ? "Verified. The ride is now marked paid."
      : `PayHere notice recorded: ${result.outcome}. The ride was not marked paid.`, !["Paid", "AlreadyPaid"].includes(result.outcome));
  } catch (error) { feedback(error.message, true); }
  finally { setBusy(false); }
});
(async () => {
  try {
    const config = await api("/config");
    byId("configuration").textContent = `${config.message} ${config.receipts}`;
    byId("configuration").classList.toggle("warn", !config.configured);
  } catch (error) { byId("configuration").textContent = error.message; byId("configuration").classList.add("warn"); }
  if (query.has("result")) {
    byId("return-notice").hidden = false;
    byId("return-notice").textContent = query.get("result") === "cancel"
      ? "You returned from PayHere without finishing checkout. You can reopen the same order below."
      : "You returned from PayHere. The ride is marked paid only when PayHere’s server notice is verified. Locally, use step 03 to send that notice.";
  }
  if (tripId) {
    setBusy(true);
    try { showTrip(await api(`/trips/${encodeURIComponent(tripId)}`)); await loadConfirmation(); watchReceipt(); feedback("Your test ride is restored. Reopening checkout reuses the same PayHere order."); }
    catch (error) { tripId = null; feedback(error.message, true); }
    finally { setBusy(false); }
  }
})();
