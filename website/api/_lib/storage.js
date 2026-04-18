'use strict';

const { createClient } = require('redis');

const DELIVERED_PREFIX = 'ks:delivered:order:';
const ISSUED_LOG = 'ks:issued_log';
const MINT_ONLY_LOG = 'ks:mint_only_log';

let _client = null;
async function client() {
  if (_client && _client.isOpen) return _client;
  const c = createClient({ url: process.env.REDIS_URL });
  c.on('error', (err) => console.error('redis client error', { err: err?.message }));
  await c.connect();
  _client = c;
  return c;
}

async function getDelivered(orderId) {
  const c = await client();
  const raw = await c.get(`${DELIVERED_PREFIX}${orderId}`);
  return raw ? JSON.parse(raw) : null;
}

async function recordDelivered(orderId, record) {
  const c = await client();
  const entry = { orderId, ...record };
  await c
    .multi()
    .set(`${DELIVERED_PREFIX}${orderId}`, JSON.stringify(record))
    .lPush(ISSUED_LOG, JSON.stringify(entry))
    .exec();
}

async function recordMintOnly(orderId, record) {
  const c = await client();
  const entry = { orderId, ...record };
  await c.lPush(MINT_ONLY_LOG, JSON.stringify(entry));
}

module.exports = { getDelivered, recordDelivered, recordMintOnly };
