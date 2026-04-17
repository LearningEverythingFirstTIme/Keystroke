'use strict';

const { createPrivateKey, randomFillSync, sign } = require('node:crypto');

const PAYLOAD_LENGTH = 14;
const SIGNATURE_LENGTH = 64;
const KEY_PREFIX = 'KS-';
const B32_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

const TIER_PRO = 1;

function buildPayload(tier) {
  const payload = Buffer.alloc(PAYLOAD_LENGTH);
  payload[0] = 1;
  payload[1] = tier;
  randomFillSync(payload, 2, 8);
  const now = Math.floor(Date.now() / 1000) >>> 0;
  payload.writeUInt32BE(now, 10);
  return payload;
}

function base32Encode(data) {
  const outLen = Math.trunc((data.length * 8 + 4) / 5);
  const chars = new Array(outLen);
  let bitBuffer = 0;
  let bitsInBuffer = 0;
  let index = 0;
  for (const b of data) {
    bitBuffer = (bitBuffer << 8) | b;
    bitsInBuffer += 8;
    while (bitsInBuffer >= 5) {
      bitsInBuffer -= 5;
      chars[index++] = B32_ALPHABET[(bitBuffer >> bitsInBuffer) & 0x1f];
    }
  }
  if (bitsInBuffer > 0) {
    chars[index++] = B32_ALPHABET[(bitBuffer << (5 - bitsInBuffer)) & 0x1f];
  }
  return chars.slice(0, index).join('');
}

function formatKey(base32) {
  const groups = [];
  for (let i = 0; i < base32.length; i += 8) {
    groups.push(base32.slice(i, i + 8));
  }
  return KEY_PREFIX + groups.join('-');
}

function normalizePrivateKeyInput(raw) {
  if (typeof raw !== 'string' || raw.length === 0) {
    throw new Error('private key is empty');
  }
  let value = raw.trim();
  if (value.includes('\\n') && !value.includes('\n')) {
    value = value.replace(/\\n/g, '\n');
  }
  if (value.includes('\r\n')) {
    value = value.replace(/\r\n/g, '\n');
  }
  if (!value.startsWith('-----BEGIN')) {
    try {
      const decoded = Buffer.from(value, 'base64').toString('utf8');
      if (decoded.startsWith('-----BEGIN')) {
        value = decoded;
      }
    } catch {
      // fall through — createPrivateKey will surface a descriptive error
    }
  }
  const headerMatch = value.match(/^(-----BEGIN [^-]+-----)/);
  const footerMatch = value.match(/(-----END [^-]+-----)\s*$/);
  if (headerMatch && footerMatch) {
    const header = headerMatch[1];
    const footer = footerMatch[1];
    const body = value.slice(header.length, value.length - footer.length).replace(/\s+/g, '');
    if (body.length > 0) {
      const wrapped = body.match(/.{1,64}/g).join('\n');
      value = `${header}\n${wrapped}\n${footer}\n`;
    }
  }
  return value;
}

function mintKey(privateKeyPem, tier) {
  const keyObject = createPrivateKey(normalizePrivateKeyInput(privateKeyPem));
  const payload = buildPayload(tier);
  const signature = sign('sha256', payload, {
    key: keyObject,
    dsaEncoding: 'ieee-p1363',
  });
  if (signature.length !== SIGNATURE_LENGTH) {
    throw new Error(
      `Unexpected signature length ${signature.length}; expected ${SIGNATURE_LENGTH}`,
    );
  }
  const combined = Buffer.concat([payload, signature]);
  return formatKey(base32Encode(combined));
}

function mintProKey(privateKeyPem) {
  return mintKey(privateKeyPem, TIER_PRO);
}

module.exports = { mintProKey, mintKey, base32Encode, formatKey };
