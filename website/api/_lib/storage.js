'use strict';

const { createClient } = require('redis');

const DELIVERED_PREFIX = 'ks:delivered:order:';
const ISSUED_LOG = 'ks:issued_log';
const MINT_ONLY_LOG = 'ks:mint_only_log';
const AUDIT_LOG_CAP = 10000;
// Long enough for a slow Resend call + DB write; short enough that a crashed
// handler releases the slot before LS's next retry.
const CLAIM_TTL_SECONDS = 90;

let _clientPromise = null;

function client() {
  if (_clientPromise) return _clientPromise;
  _clientPromise = (async () => {
    const c = createClient({ url: process.env.REDIS_URL });
    c.on('error', (err) => {
      console.error('redis client error', { err: err?.message });
      _clientPromise = null;
    });
    c.on('end', () => { _clientPromise = null; });
    await c.connect();
    return c;
  })().catch((err) => {
    _clientPromise = null;
    throw err;
  });
  return _clientPromise;
}

async function getDelivered(orderId) {
  const c = await client();
  const raw = await c.get(`${DELIVERED_PREFIX}${orderId}`);
  return raw ? JSON.parse(raw) : null;
}

// Atomically claim the delivery slot. Returns true iff we won the claim.
// A false return means either a concurrent handler is in-flight or the
// order has already been fulfilled (the caller's getDelivered fast-path
// will have already caught the completed case).
async function claimDelivered(orderId) {
  const c = await client();
  const placeholder = JSON.stringify({ status: 'claiming', claimedAt: Date.now() });
  const result = await c.set(`${DELIVERED_PREFIX}${orderId}`, placeholder, {
    NX: true,
    EX: CLAIM_TTL_SECONDS,
  });
  return result === 'OK';
}

async function releaseClaim(orderId) {
  const c = await client();
  await c.del(`${DELIVERED_PREFIX}${orderId}`);
}

async function recordDelivered(orderId, record) {
  const c = await client();
  const entry = { orderId, ...record };
  await c
    .multi()
    .set(`${DELIVERED_PREFIX}${orderId}`, JSON.stringify(record))
    .lPush(ISSUED_LOG, JSON.stringify(entry))
    .lTrim(ISSUED_LOG, 0, AUDIT_LOG_CAP - 1)
    .exec();
}

async function recordMintOnly(orderId, record) {
  const c = await client();
  const entry = { orderId, ...record };
  await c
    .multi()
    .lPush(MINT_ONLY_LOG, JSON.stringify(entry))
    .lTrim(MINT_ONLY_LOG, 0, AUDIT_LOG_CAP - 1)
    .exec();
}

module.exports = { getDelivered, claimDelivered, releaseClaim, recordDelivered, recordMintOnly };
