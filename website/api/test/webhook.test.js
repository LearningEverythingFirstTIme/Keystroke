'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { createHmac, generateKeyPairSync } = require('node:crypto');
const { Readable } = require('node:stream');

// ---- module mocks ----
// Replace @vercel/kv and resend in the require cache before loading lemon-webhook.

const kvState = {
  delivered: new Map(),
  issuedLog: [],
  mintOnlyLog: [],
  getShouldThrow: false,
};

function resetKv() {
  kvState.delivered.clear();
  kvState.issuedLog.length = 0;
  kvState.mintOnlyLog.length = 0;
  kvState.getShouldThrow = false;
}

const kvMock = {
  isOpen: true,
  async connect() {},
  on() {},
  async get(key) {
    if (kvState.getShouldThrow) throw new Error('kv down');
    return kvState.delivered.get(key) ?? null;
  },
  async set(key, value, options) {
    // Fake atomicity: real redis enforces NX server-side; here the event-loop
    // ordering of the two awaits in Promise.all([set, set]) gives us the same
    // winner/loser behavior in practice.
    if (options && options.NX && kvState.delivered.has(key)) return null;
    kvState.delivered.set(key, value);
    return 'OK';
  },
  async del(key) {
    return kvState.delivered.delete(key) ? 1 : 0;
  },
  async lPush(key, value) {
    if (key.endsWith('issued_log')) kvState.issuedLog.unshift(value);
    else if (key.endsWith('mint_only_log')) kvState.mintOnlyLog.unshift(value);
    return 1;
  },
  async lTrim(key, start, stop) {
    const list = key.endsWith('issued_log') ? kvState.issuedLog
      : key.endsWith('mint_only_log') ? kvState.mintOnlyLog : null;
    if (list && list.length > stop + 1) list.length = stop + 1;
    return 'OK';
  },
  multi() {
    const ops = [];
    const chain = {
      set: (k, v, opts) => { ops.push(['set', k, v, opts]); return chain; },
      lPush: (k, v) => { ops.push(['lPush', k, v]); return chain; },
      lTrim: (k, start, stop) => { ops.push(['lTrim', k, start, stop]); return chain; },
      exec: async () => {
        const results = [];
        for (const op of ops) {
          if (op[0] === 'set') results.push(await kvMock.set(op[1], op[2], op[3]));
          else if (op[0] === 'lPush') results.push(await kvMock.lPush(op[1], op[2]));
          else if (op[0] === 'lTrim') results.push(await kvMock.lTrim(op[1], op[2], op[3]));
        }
        return results;
      },
    };
    return chain;
  },
};

const resendState = {
  sent: [],
  sendShouldReturnError: false,
  sendShouldThrow: false,
};

function resetResend() {
  resendState.sent.length = 0;
  resendState.sendShouldReturnError = false;
  resendState.sendShouldThrow = false;
}

class ResendMock {
  constructor(apiKey) { this.apiKey = apiKey; }
  get emails() {
    return {
      send: async (payload) => {
        if (resendState.sendShouldThrow) throw new Error('network fail');
        resendState.sent.push(payload);
        if (resendState.sendShouldReturnError) {
          return { error: { message: 'resend rejected' } };
        }
        return { data: { id: `resend-${resendState.sent.length}` } };
      },
    };
  }
}

// Inject mocks via require.cache
const redisModulePath = require.resolve('redis');
require.cache[redisModulePath] = {
  id: redisModulePath,
  filename: redisModulePath,
  loaded: true,
  exports: { createClient: () => kvMock },
};

const resendModulePath = require.resolve('resend');
require.cache[resendModulePath] = {
  id: resendModulePath,
  filename: resendModulePath,
  loaded: true,
  exports: { Resend: ResendMock },
};

// Now safe to load the handler
const handler = require('../lemon-webhook');

// ---- env setup ----
const TEST_SECRET = 'test-webhook-secret';
const { privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
const TEST_PEM = privateKey.export({ format: 'pem', type: 'pkcs8' });

process.env.LS_WEBHOOK_SECRET = TEST_SECRET;
process.env.KS_PRIVATE_KEY = TEST_PEM;
process.env.RESEND_API_KEY = 'test-resend-key';

// ---- helpers ----
function makePayload({ eventName = 'order_created', orderId = '12345', email = 'buyer@example.com', createdAt, userName } = {}) {
  return {
    meta: { event_name: eventName },
    data: {
      id: orderId,
      attributes: {
        user_email: email,
        user_name: userName,
        order_number: Number(orderId),
        created_at: createdAt ?? new Date().toISOString(),
      },
    },
  };
}

function signBody(body, secret = TEST_SECRET) {
  return createHmac('sha256', secret).update(body).digest('hex');
}

function makeReq({ method = 'POST', body, signature, overrideSignature } = {}) {
  const rawBody = typeof body === 'string' ? body : JSON.stringify(body);
  const buf = Buffer.from(rawBody, 'utf8');
  const sig = overrideSignature !== undefined ? overrideSignature : (signature ?? signBody(buf));
  const stream = Readable.from([buf]);
  stream.method = method;
  stream.headers = { 'x-signature': sig };
  return stream;
}

function makeRes() {
  const res = {
    statusCode: null,
    headers: {},
    body: null,
    status(code) { this.statusCode = code; return this; },
    setHeader(k, v) { this.headers[k] = v; return this; },
    send(body) { this.body = body; return this; },
  };
  return res;
}

// ---- tests ----

test.beforeEach(() => {
  resetKv();
  resetResend();
});

test('rejects non-POST with 405', async () => {
  const req = makeReq({ method: 'GET', body: '' });
  const res = makeRes();
  await handler(req, res);
  assert.equal(res.statusCode, 405);
  assert.equal(res.headers.Allow, 'POST');
});

test('rejects bad signature with 401', async () => {
  const req = makeReq({ body: makePayload(), overrideSignature: 'deadbeef'.repeat(8) });
  const res = makeRes();
  await handler(req, res);
  assert.equal(res.statusCode, 401);
});

test('rejects missing signature with 401', async () => {
  const req = makeReq({ body: makePayload(), overrideSignature: '' });
  const res = makeRes();
  await handler(req, res);
  assert.equal(res.statusCode, 401);
});

test('ignores non-order_created events with 200', async () => {
  const req = makeReq({ body: makePayload({ eventName: 'subscription_created' }) });
  const res = makeRes();
  await handler(req, res);
  assert.equal(res.statusCode, 200);
  assert.equal(res.body, 'ignored');
  assert.equal(resendState.sent.length, 0);
});

test('missing user_email returns 200 (no retry)', async () => {
  const req = makeReq({ body: makePayload({ email: '' }) });
  const res = makeRes();
  await handler(req, res);
  assert.equal(res.statusCode, 200);
  assert.equal(res.body, 'missing email');
  assert.equal(resendState.sent.length, 0);
});

test('happy path — mints key, sends email, records delivery', async () => {
  const payload = makePayload({ orderId: 'order-happy', email: 'happy@example.com', userName: 'Alice' });
  const req = makeReq({ body: payload });
  const res = makeRes();
  await handler(req, res);
  assert.equal(res.statusCode, 200);
  assert.equal(res.body, 'ok');
  assert.equal(resendState.sent.length, 1);

  const sent = resendState.sent[0];
  assert.equal(sent.to, 'happy@example.com');
  assert.match(sent.text, /Thanks for picking up Keystroke Pro, Alice/);
  assert.match(sent.text, /KS-[A-Z2-7]/);
  assert.match(sent.text, /Settings \u2192 Advanced/);

  // Audit trail populated
  assert.equal(kvState.delivered.size, 1);
  const record = JSON.parse(kvState.delivered.get('ks:delivered:order:order-happy'));
  assert.equal(record.email, 'happy@example.com');
  assert.equal(record.resendId, 'resend-1');
  assert.equal(kvState.issuedLog.length, 1);
  // License key must NOT be in the stored record
  assert.equal(record.licenseKey, undefined);
  assert.equal(record.key, undefined);
});

test('duplicate delivery — second request is a no-op', async () => {
  const payload = makePayload({ orderId: 'order-dup' });

  // First request
  await handler(makeReq({ body: payload }), makeRes());
  assert.equal(resendState.sent.length, 1);

  // Second request, identical payload and signature
  const res2 = makeRes();
  await handler(makeReq({ body: payload }), res2);
  assert.equal(res2.statusCode, 200);
  assert.equal(res2.body, 'already issued');
  assert.equal(resendState.sent.length, 1, 'no second email');
  assert.equal(kvState.issuedLog.length, 1, 'no second audit entry');
});

test('concurrent deliveries of the same order issue exactly one key', async () => {
  const payload = makePayload({ orderId: 'order-race' });

  const [, ] = await Promise.all([
    handler(makeReq({ body: payload }), makeRes()),
    handler(makeReq({ body: payload }), makeRes()),
  ]);

  assert.equal(resendState.sent.length, 1, 'exactly one email sent under concurrent retries');
  assert.equal(kvState.delivered.size, 1, 'exactly one delivered record');
  assert.equal(kvState.issuedLog.length, 1, 'exactly one audit entry');
});

test('delayed webhook (old created_at) still fulfills the order', async () => {
  const tenMinAgo = new Date(Date.now() - 10 * 60 * 1000).toISOString();
  const req = makeReq({ body: makePayload({ orderId: 'order-delayed', createdAt: tenMinAgo }) });
  const res = makeRes();
  await handler(req, res);
  assert.equal(res.statusCode, 200);
  assert.equal(res.body, 'ok');
  assert.equal(resendState.sent.length, 1);
  assert.equal(kvState.issuedLog.length, 1);
});

test('future-dated webhook (beyond slop) is rejected', async () => {
  const fiveMinAhead = new Date(Date.now() + 5 * 60 * 1000).toISOString();
  const req = makeReq({ body: makePayload({ createdAt: fiveMinAhead }) });
  const res = makeRes();
  await handler(req, res);
  assert.equal(res.statusCode, 200);
  assert.equal(res.body, 'stale');
});

test('resend error — logs to mint-only audit log, returns 500', async () => {
  resendState.sendShouldReturnError = true;
  const req = makeReq({ body: makePayload({ orderId: 'order-fail' }) });
  const res = makeRes();
  await handler(req, res);
  assert.equal(res.statusCode, 500);
  assert.equal(kvState.delivered.size, 0, 'no delivered record');
  assert.equal(kvState.mintOnlyLog.length, 1, 'mint-only audit entry written');
  const entry = JSON.parse(kvState.mintOnlyLog[0]);
  assert.equal(entry.orderId, 'order-fail');
  assert.equal(entry.email, 'buyer@example.com');
});

test('resend throws — logs to mint-only audit log, returns 500', async () => {
  resendState.sendShouldThrow = true;
  const req = makeReq({ body: makePayload({ orderId: 'order-throw' }) });
  const res = makeRes();
  await handler(req, res);
  assert.equal(res.statusCode, 500);
  assert.equal(kvState.mintOnlyLog.length, 1);
});

test('kv lookup failure — returns 500 (no key issued)', async () => {
  kvState.getShouldThrow = true;
  const req = makeReq({ body: makePayload() });
  const res = makeRes();
  await handler(req, res);
  assert.equal(res.statusCode, 500);
  assert.equal(resendState.sent.length, 0, 'no email sent when storage unavailable');
});

test('keygen failure — returns 200 to stop LS retry', async () => {
  const saved = process.env.KS_PRIVATE_KEY;
  process.env.KS_PRIVATE_KEY = 'not a real pem';
  try {
    const req = makeReq({ body: makePayload({ orderId: 'order-keygen-fail' }) });
    const res = makeRes();
    await handler(req, res);
    assert.equal(res.statusCode, 200);
    assert.equal(res.body, 'keygen failed (not retrying)');
    assert.equal(resendState.sent.length, 0);
  } finally {
    process.env.KS_PRIVATE_KEY = saved;
  }
});

test('missing env vars — returns 200 to stop LS retry', async () => {
  const saved = process.env.LS_WEBHOOK_SECRET;
  delete process.env.LS_WEBHOOK_SECRET;
  try {
    const req = makeReq({ body: makePayload(), overrideSignature: 'doesnt-matter' });
    const res = makeRes();
    await handler(req, res);
    assert.equal(res.statusCode, 200);
    assert.equal(res.body, 'misconfigured');
  } finally {
    process.env.LS_WEBHOOK_SECRET = saved;
  }
});

test('malformed JSON body returns 400', async () => {
  const req = makeReq({ body: '{not json' });
  const res = makeRes();
  await handler(req, res);
  assert.equal(res.statusCode, 400);
});

test('internal test exports (verifySignature, renderEmail) are available', () => {
  const { verifySignature, renderEmail, extractFirstName } = handler.__test__;
  assert.equal(verifySignature(Buffer.from('x'), 'bad', 'secret'), false);
  const email = renderEmail({ key: 'KS-TEST', orderRef: '#1', firstName: 'Bob' });
  assert.match(email.text, /Bob/);
  assert.match(email.text, /KS-TEST/);
  assert.match(email.html, /KS-TEST/);
  assert.equal(extractFirstName({ user_name: 'Jane Doe' }), 'Jane');
  assert.equal(extractFirstName({ user_name: '' }), null);
});

test('email escapes HTML in order reference', () => {
  const { renderEmail } = handler.__test__;
  const email = renderEmail({ key: 'KS-ABC', orderRef: '<script>alert(1)</script>', firstName: null });
  assert.match(email.html, /&lt;script&gt;/);
  assert.doesNotMatch(email.html, /<script>/);
});
