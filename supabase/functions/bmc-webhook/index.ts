// Buy Me a Coffee calls this for every payment (Webhooks in BMC's developer settings on
// buymeacoffee.com). It records the payer as a MyRGB supporter, matched by the email BMC sends
// with the payment.
//
//   supabase secrets set BMC_WEBHOOK_SECRET=<the signing secret from BMC's webhook settings>
//   supabase functions deploy bmc-webhook --no-verify-jwt
//
// BMC doesn't send a Supabase token, hence --no-verify-jwt; the signed body is the check: the
// x-signature-sha256 header is the HMAC-SHA256 of the raw body with that secret.
// Monthly support counts 35 days from each renewal (a few days' grace past the next one), yearly
// support 368 from each payment, and a one-off coffee a year. Cancels and pauses need no event:
// support simply runs out.

import { createClient } from "jsr:@supabase/supabase-js@2";

const MONTHLY_DAYS = 35;
const YEARLY_DAYS = 368;
const ONE_OFF_DAYS = 365;

interface BmcEvent {
  type: string;
  live_mode: boolean;
  data: {
    supporter_email?: string;
    transaction_id?: string;
    id?: number;
    duration_type?: string;
    current_period_start?: number;
  };
}

Deno.serve(async (req) => {
  if (req.method !== "POST") return new Response("Method not allowed", { status: 405 });

  const body = await req.text();
  if (!await signed(body, req.headers.get("x-signature-sha256"))) {
    return new Response("Unauthorized", { status: 401 });
  }

  let event: BmcEvent;
  try {
    event = JSON.parse(body);
  } catch {
    return new Response("Bad request", { status: 400 });
  }

  if (!event.live_mode) return new Response("OK (test event)", { status: 200 });

  const payment = describe(event);
  if (!payment) return new Response("OK (nothing to record)", { status: 200 });
  if (!payment.email) return new Response("OK (no email to match)", { status: 200 });

  const db = createClient(Deno.env.get("SUPABASE_URL")!, Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!, {
    auth: { persistSession: false },
  });

  // BMC retries on any hiccup; a payment already recorded answers itself instead of extending twice.
  const { data: seen } = await db.from("supporters").select("user_id").eq("reference", payment.reference).limit(1).maybeSingle();
  if (!seen) {
    const { data: seenClaim } = await db.from("supporter_claims").select("email").eq("reference", payment.reference).limit(1).maybeSingle();
    if (seenClaim) return new Response("OK (already recorded)", { status: 200 });
  } else {
    return new Response("OK (already recorded)", { status: 200 });
  }

  const userId = await findUser(db, payment.email);
  if (userId) {
    // Extend from whichever is later: now, or the end of support already on record.
    const { data: current } = await db.from("supporters").select("until").eq("user_id", userId).maybeSingle();
    if (current && current.until === null) return new Response("OK (already permanent)", { status: 200 });
    const until = extend(current?.until ?? null, payment.days);
    const { error } = await db.from("supporters")
      .upsert({ user_id: userId, until, source: "bmc", reference: payment.reference }, { onConflict: "user_id" });
    if (error) return new Response("Could not record support", { status: 500 });
  } else {
    const { data: claim } = await db.from("supporter_claims").select("until").eq("email", payment.email).maybeSingle();
    const until = extend(claim?.until ?? null, payment.days);
    const { error } = await db.from("supporter_claims")
      .upsert({ email: payment.email, until, source: "bmc", reference: payment.reference }, { onConflict: "email" });
    if (error) return new Response("Could not record support", { status: 500 });
  }
  return new Response("OK", { status: 200 });
});

// Which payments count as support, and for how long. Refunds, cancels, shop orders and
// commissions aren't support, so they record nothing; the signing secret already proved
// the event is genuine.
function describe(event: BmcEvent): { email: string; days: number; reference: string } | null {
  const email = event.data?.supporter_email?.trim().toLowerCase() ?? "";
  switch (event.type) {
    case "donation.created": {
      const id = event.data?.transaction_id ?? event.data?.id;
      if (id === undefined) return null;
      return { email, days: ONE_OFF_DAYS, reference: `bmc:donation:${id}` };
    }
    case "membership.started":
    case "membership.updated":
    case "recurring_donation.started":
    case "recurring_donation.updated": {
      const { id, current_period_start } = event.data ?? {};
      if (id === undefined || current_period_start === undefined) return null;
      const days = event.data?.duration_type === "year" ? YEARLY_DAYS : MONTHLY_DAYS;
      const kind = event.type.startsWith("membership") ? "membership" : "recurring_donation";
      return { email, days, reference: `bmc:${kind}:${id}:${current_period_start}` };
    }
    default:
      return null;
  }
}

// True when the x-signature-sha256 header is the HMAC-SHA256 of the raw body with the secret.
async function signed(body: string, header: string | null): Promise<boolean> {
  const secret = Deno.env.get("BMC_WEBHOOK_SECRET");
  if (!secret || !header) return false;
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(body));
  const expected = [...new Uint8Array(mac)].map(b => b.toString(16).padStart(2, "0")).join("");
  const given = header.trim().toLowerCase();
  let diff = expected.length ^ given.length;
  for (let i = 0; i < expected.length && i < given.length; i++)
    diff |= expected.charCodeAt(i) ^ given.charCodeAt(i);
  return diff === 0;
}

function extend(currentUntil: string | null, days: number): string {
  const from = Math.max(Date.now(), currentUntil ? Date.parse(currentUntil) : 0);
  return new Date(from + days * 86_400_000).toISOString();
}

// The auth admin API has no lookup by email, so page through users (fine at MyRGB's scale).
async function findUser(db: ReturnType<typeof createClient>, email: string): Promise<string | null> {
  for (let page = 1; page <= 50; page++) {
    const { data, error } = await db.auth.admin.listUsers({ page, perPage: 1000 });
    if (error || !data) return null;
    const hit = data.users.find((u) => u.email?.toLowerCase() === email);
    if (hit) return hit.id;
    if (data.users.length < 1000) return null;
  }
  return null;
}
