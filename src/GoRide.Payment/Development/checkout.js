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
    feedback("Test ride ready. Continue to Stripe when you’re ready.");
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
(async () => {
  try {
    const config = await api("/config");
    byId("configuration").textContent = config.message;
    byId("configuration").classList.toggle("warn", !config.configured);
  } catch (error) { byId("configuration").textContent = error.message; byId("configuration").classList.add("warn"); }
  if (query.has("result")) {
    byId("return-notice").hidden = false;
    byId("return-notice").textContent = query.get("result") === "cancel"
      ? "You returned from Stripe without finishing checkout. You can reopen the same checkout below."
      : "You returned from Stripe. This page does not verify payment or mark the ride paid; provider verification is handled in SCRUM-103.";
  }
  if (tripId) {
    setBusy(true);
    try { showTrip(await api(`/trips/${encodeURIComponent(tripId)}`)); feedback("Your test ride is restored. Reopening checkout safely reuses an active session."); }
    catch (error) { tripId = null; feedback(error.message, true); }
    finally { setBusy(false); }
  }
})();
