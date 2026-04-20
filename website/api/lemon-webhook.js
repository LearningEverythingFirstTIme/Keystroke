'use strict';

const { createHmac, timingSafeEqual } = require('node:crypto');
const { Resend } = require('resend');

const { mintProKey } = require('./_lib/keygen');
const { getDelivered, claimDelivered, releaseClaim, recordDelivered, recordMintOnly } = require('./_lib/storage');

const FROM_ADDRESS = 'Keystroke <support@keystroke-app.com>';
const SUBJECT = 'Your Keystroke Pro license key';
const REPLAY_MAX_AGE_MS = 5 * 60 * 1000;
const REPLAY_FUTURE_SLOP_MS = 60 * 1000;

async function readRawBody(req) {
  const chunks = [];
  for await (const chunk of req) {
    chunks.push(typeof chunk === 'string' ? Buffer.from(chunk) : chunk);
  }
  return Buffer.concat(chunks);
}

function verifySignature(rawBody, headerSignature, secret) {
  if (!headerSignature || !secret) return false;
  const expected = createHmac('sha256', secret).update(rawBody).digest('hex');
  const received = Buffer.from(String(headerSignature), 'utf8');
  const computed = Buffer.from(expected, 'utf8');
  if (received.length !== computed.length) return false;
  return timingSafeEqual(received, computed);
}

function renderEmail({ key, orderRef, firstName }) {
  const greeting = firstName ? `Thanks for picking up Keystroke Pro, ${firstName}.` : 'Thanks for picking up Keystroke Pro.';
  const text = [
    greeting,
    '',
    'Your license key:',
    '',
    `    ${key}`,
    '',
    'To activate: open Keystroke \u2192 Settings \u2192 Advanced \u2192 paste the key.',
    'Pro unlocks immediately and validates offline \u2014 no internet required after activation.',
    '',
    `Order reference: ${orderRef}`,
    '',
    '14-day refunds, questions, or a lost key: reply to this email or write to support@keystroke-app.com.',
    '',
    '\u2014 Nick',
  ].join('\n');

  const escape = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const html = `<!doctype html>
<html><body style="font-family:-apple-system,Segoe UI,Helvetica,Arial,sans-serif;font-size:15px;line-height:1.55;color:#111;max-width:560px;margin:0 auto;padding:24px;">
<p>${escape(greeting)}</p>
<p>Your license key:</p>
<pre style="background:#f4f4f5;border:1px solid #e4e4e7;border-radius:6px;padding:12px 14px;font-size:14px;overflow-x:auto;user-select:all;">${escape(key)}</pre>
<p>To activate: open Keystroke &rarr; Settings &rarr; Advanced &rarr; paste the key.<br>
Pro unlocks immediately and validates offline &mdash; no internet required after activation.</p>
<p style="color:#52525b;font-size:13px;">Order reference: ${escape(orderRef)}</p>
<p style="color:#52525b;font-size:13px;">14-day refunds, questions, or a lost key: reply to this email or write to <a href="mailto:support@keystroke-app.com" style="color:#2563eb;">support@keystroke-app.com</a>.</p>
<p>&mdash; Nick</p>
</body></html>`;

  return { text, html };
}

function extractFirstName(attrs) {
  const userName = typeof attrs?.user_name === 'string' ? attrs.user_name.trim() : '';
  if (!userName) return null;
  const first = userName.split(/\s+/)[0];
  return first && first.length <= 40 ? first : null;
}

async function handler(req, res) {
  if (req.method !== 'POST') {
    res.setHeader('Allow', 'POST');
    res.status(405).send('Method Not Allowed');
    return;
  }

  const secret = process.env.LS_WEBHOOK_SECRET;
  const privateKeyPem = process.env.KS_PRIVATE_KEY;
  const resendApiKey = process.env.RESEND_API_KEY;

  if (!secret || !privateKeyPem || !resendApiKey) {
    console.error('webhook misconfigured: missing env vars', {
      hasSecret: Boolean(secret),
      hasPrivateKey: Boolean(privateKeyPem),
      hasResendApiKey: Boolean(resendApiKey),
    });
    // 200 to stop LS retry loop on deterministic config failures; Vercel error log is the signal.
    res.status(200).send('misconfigured');
    return;
  }

  let rawBody;
  try {
    rawBody = await readRawBody(req);
  } catch (err) {
    console.error('failed to read request body', err);
    res.status(400).send('Bad Request');
    return;
  }

  const signature = req.headers['x-signature'];
  if (!verifySignature(rawBody, signature, secret)) {
    console.warn('webhook signature verification failed');
    res.status(401).send('Unauthorized');
    return;
  }

  let payload;
  try {
    payload = JSON.parse(rawBody.toString('utf8'));
  } catch (err) {
    console.error('invalid JSON in webhook body', err);
    res.status(400).send('Bad Request');
    return;
  }

  const eventName = payload?.meta?.event_name;
  if (eventName !== 'order_created') {
    console.log('ignoring event', eventName);
    res.status(200).send('ignored');
    return;
  }

  const attrs = payload?.data?.attributes ?? {};
  const orderId = payload?.data?.id != null ? String(payload.data.id) : '';
  const orderRef = attrs.order_number != null
    ? `#${attrs.order_number}`
    : orderId
      ? `#${orderId}`
      : 'unknown';

  if (!orderId) {
    console.error('order_created missing data.id', { orderRef });
    res.status(200).send('missing order id');
    return;
  }

  const createdAtRaw = typeof attrs?.created_at === 'string' ? attrs.created_at : null;
  const createdAt = createdAtRaw ? Date.parse(createdAtRaw) : NaN;
  if (Number.isFinite(createdAt)) {
    const ageMs = Date.now() - createdAt;
    if (ageMs < -REPLAY_FUTURE_SLOP_MS) {
      console.warn('rejecting future-dated webhook', { orderRef, ageMs });
      res.status(200).send('stale');
      return;
    }

    if (ageMs > REPLAY_MAX_AGE_MS) {
      console.warn('processing delayed webhook', { orderRef, ageMs });
    }
  }

  const email = typeof attrs.user_email === 'string' ? attrs.user_email.trim() : '';
  if (!email) {
    console.error('order_created missing user_email', { orderRef });
    res.status(200).send('missing email');
    return;
  }

  try {
    const existing = await getDelivered(orderId);
    if (existing && existing.status === 'done') {
      console.log('duplicate delivery (already issued)', { orderRef, resendId: existing.resendId });
      res.status(200).send('already issued');
      return;
    }
  } catch (err) {
    console.error('kv lookup failed', { orderRef, err: err?.message });
    // Fail closed: if we can't check idempotency, don't issue a key. LS will retry.
    res.status(500).send('Storage unavailable');
    return;
  }

  let claimed;
  try {
    claimed = await claimDelivered(orderId);
  } catch (err) {
    console.error('kv claim failed', { orderRef, err: err?.message });
    res.status(500).send('Storage unavailable');
    return;
  }
  if (!claimed) {
    // A concurrent handler won the claim. LS will retry; the next attempt
    // will hit the 'already issued' fast-path once that handler finalizes.
    console.log('concurrent delivery in progress', { orderRef });
    res.status(200).send('in progress');
    return;
  }

  let licenseKey;
  try {
    licenseKey = mintProKey(privateKeyPem);
  } catch (err) {
    console.error('keygen failed', { orderRef, err: err?.message });
    await releaseClaim(orderId).catch((relErr) => {
      console.error('failed to release claim after keygen failure', { orderRef, err: relErr?.message });
    });
    // 200 so LS stops retrying a deterministic config failure. Vercel error alert is the signal.
    res.status(200).send('keygen failed (not retrying)');
    return;
  }

  const { text, html } = renderEmail({ key: licenseKey, orderRef, firstName: extractFirstName(attrs) });

  let resendId;
  try {
    const resend = new Resend(resendApiKey);
    const result = await resend.emails.send({
      from: FROM_ADDRESS,
      to: email,
      subject: SUBJECT,
      text,
      html,
    });
    if (result?.error) {
      console.error('resend error', { orderRef, email, err: String(result.error?.message || result.error) });
      await releaseClaim(orderId).catch((relErr) => {
        console.error('failed to release claim after resend error', { orderRef, err: relErr?.message });
      });
      await recordMintOnly(orderId, { email, attemptedAt: Date.now(), error: String(result.error?.message || result.error) }).catch((logErr) => {
        console.error('failed to record mint-only audit entry', { orderRef, err: logErr?.message });
      });
      res.status(500).send('Email send failed');
      return;
    }
    resendId = result?.data?.id;
  } catch (err) {
    console.error('resend threw', { orderRef, email, err: err?.message });
    await releaseClaim(orderId).catch((relErr) => {
      console.error('failed to release claim after resend threw', { orderRef, err: relErr?.message });
    });
    await recordMintOnly(orderId, { email, attemptedAt: Date.now(), error: String(err?.message || 'unknown') }).catch((logErr) => {
      console.error('failed to record mint-only audit entry', { orderRef, err: logErr?.message });
    });
    res.status(500).send('Email send failed');
    return;
  }

  try {
    await recordDelivered(orderId, { status: 'done', email, issuedAt: Date.now(), resendId: resendId ?? null });
  } catch (err) {
    // Email was sent successfully — losing the audit entry is bad but don't double-send on retry.
    // The claim placeholder will expire after CLAIM_TTL_SECONDS, but by then LS has stopped
    // retrying on our 200 below. We can't undo the send, so log loudly.
    console.error('failed to record delivered (email was sent)', { orderRef, resendId, err: err?.message });
  }

  console.log('license key fulfilled', { orderRef, email, resendId });
  res.status(200).send('ok');
}

module.exports = handler;
module.exports.config = {
  api: {
    bodyParser: false,
  },
};
module.exports.__test__ = { verifySignature, renderEmail, extractFirstName };
