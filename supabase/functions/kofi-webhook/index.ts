// Ko-fi calls this for every payment (Settings > API > Webhooks on ko-fi.com). It records the
// payer as a MyRGB supporter, matched by the email Ko-fi sends with the payment.
//
//   supabase secrets set KOFI_VERIFICATION_TOKEN=<the token from Ko-fi's webhook settings>
//   supabase functions deploy kofi-webhook --no-verify-jwt
//
// Ko-fi doesn't send a Supabase token, hence --no-verify-jwt; the verification token is the check.
// Monthly memberships count for 35 days from each payment (a few days' grace past the next one);
// a one-off donation counts for a year.

import { createClient } from "jsr:@supabase/supabase-js@2";

const MONTHLY_DAYS = 35;
const ONE_OFF_DAYS = 365;

interface KofiPayment {
  verification_token: string;
  kofi_transaction_id: string;
  type: string; // "Donation" | "Subscription" | "Commission" | "Shop Order"
  email?: string;
  is_subscription_payment?: boolean;
}

Deno.serve(async (req) => {
  if (req.method !== "POST") return new Response("Method not allowed", { status: 405 });

  // Ko-fi posts a form with one field, "data", holding the payment as JSON.
  let payment: KofiPayment;
  try {
    const form = await req.formData();
    payment = JSON.parse(String(form.get("data") ?? ""));
  } catch {
    return new Response("Bad request", { status: 400 });
  }

  const expected = Deno.env.get("KOFI_VERIFICATION_TOKEN");
  if (!expected || payment.verification_token !== expected) {
    return new Response("Unauthorized", { status: 401 });
  }

  const email = payment.email?.trim().toLowerCase();
  if (!email) return new Response("OK (no email to match)", { status: 200 });
  if (!payment.kofi_transaction_id) return new Response("OK (nothing to record)", { status: 200 });

  const days = payment.is_subscription_payment || payment.type === "Subscription" ? MONTHLY_DAYS : ONE_OFF_DAYS;
  const reference = `kofi:${payment.kofi_transaction_id}`.slice(0, 200);

  const db = createClient(Deno.env.get("SUPABASE_URL")!, Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!, {
    auth: { persistSession: false },
  });

  // Ko-fi retries on any hiccup; a payment already recorded answers itself instead of extending twice.
  const { data: seen } = await db.from("supporters").select("user_id").eq("reference", reference).limit(1).maybeSingle();
  if (!seen) {
    const { data: seenClaim } = await db.from("supporter_claims").select("email").eq("reference", reference).limit(1).maybeSingle();
    if (seenClaim) return new Response("OK (already recorded)", { status: 200 });
  } else {
    return new Response("OK (already recorded)", { status: 200 });
  }

  const userId = await findUser(db, email);
  if (userId) {
    // Extend from whichever is later: now, or the end of support already on record.
    const { data: current } = await db.from("supporters").select("until").eq("user_id", userId).maybeSingle();
    if (current && current.until === null) return new Response("OK (already permanent)", { status: 200 });
    const until = extend(current?.until ?? null, days);
    const { error } = await db.from("supporters")
      .upsert({ user_id: userId, until, source: "kofi", reference }, { onConflict: "user_id" });
    if (error) return new Response("Could not record support", { status: 500 });
  } else {
    const { data: claim } = await db.from("supporter_claims").select("until").eq("email", email).maybeSingle();
    const until = extend(claim?.until ?? null, days);
    const { error } = await db.from("supporter_claims")
      .upsert({ email, until, source: "kofi", reference }, { onConflict: "email" });
    if (error) return new Response("Could not record support", { status: 500 });
  }
  return new Response("OK", { status: 200 });
});

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
