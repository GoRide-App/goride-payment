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
  if (!response.ok) throw new Error(data?.title || "The request could not be completed. Please retry.");
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
    const trip = await api("/trips", { finalFare: Number(byId("fare").value) });
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
byId("simulate").addEventListener("click", async () => {
  if (busy || !tripId) return;
  setBusy(true);
  feedback("Sending the signed PayHere notice…");
  try {
    const result = await api(`/trips/${encodeURIComponent(tripId)}/simulate-notify`, { statusCode: 2 });
    showTrip(result.payment);
    await loadConfirmation();
    feedback(result.outcome === "Paid" || result.outcome === "AlreadyPaid"
      ? "Verified. The ride is now marked paid."
      : `PayHere notice recorded: ${result.outcome}. The ride was not marked paid.`, !["Paid", "AlreadyPaid"].includes(result.outcome));
  } catch (error) { feedback(error.message, true); }
  finally { setBusy(false); }
});
(async () => {
  try {
    const config = await api("/config");
    byId("configuration").textContent = config.message;
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
    try { showTrip(await api(`/trips/${encodeURIComponent(tripId)}`)); await loadConfirmation(); feedback("Your test ride is restored. Reopening checkout reuses the same PayHere order."); }
    catch (error) { tripId = null; feedback(error.message, true); }
    finally { setBusy(false); }
  }
})();
