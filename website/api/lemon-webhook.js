'use strict';

const { createHmac, timingSafeEqual } = require('node:crypto');
const { Resend } = require('resend');

const { mintProKey } = require('./_lib/keygen');

const FROM_ADDRESS = 'Keystroke <support@keystroke-app.com>';
const SUBJECT = 'Your Keystroke Pro license key';

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

module.exports = async function handler(req, res) {
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
    res.status(500).send('Server misconfigured');
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
  const email = typeof attrs.user_email === 'string' ? attrs.user_email.trim() : '';
  const orderRef = attrs.order_number != null
    ? `#${attrs.order_number}`
    : payload?.data?.id
      ? `#${payload.data.id}`
      : 'unknown';

  if (!email) {
    console.error('order_created missing user_email', { orderRef });
    res.status(200).send('missing email');
    return;
  }

  let licenseKey;
  try {
    licenseKey = mintProKey(privateKeyPem);
  } catch (err) {
    const raw = privateKeyPem ?? '';
    const shape = {
      length: raw.length,
      startsWithBegin: raw.startsWith('-----BEGIN'),
      hasNewline: raw.includes('\n'),
      hasCrlf: raw.includes('\r\n'),
      hasEscapedNewline: raw.includes('\\n'),
      newlineCount: (raw.match(/\n/g) || []).length,
      first40: raw.slice(0, 40),
      last40: raw.slice(-40),
    };
    console.error('keygen failed', { shape, err: err?.message });
    res.status(500).send('Keygen failed');
    return;
  }

  const { text, html } = renderEmail({ key: licenseKey, orderRef, firstName: extractFirstName(attrs) });

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
      console.error('resend error', { orderRef, email, licenseKey, error: result.error });
      res.status(500).send('Email send failed');
      return;
    }
    console.log('license key fulfilled', { orderRef, email, licenseKey, resendId: result?.data?.id });
  } catch (err) {
    console.error('resend threw', { orderRef, email, licenseKey, err: err?.message });
    res.status(500).send('Email send failed');
    return;
  }

  res.status(200).send('ok');
};

module.exports.config = {
  api: {
    bodyParser: false,
  },
};
