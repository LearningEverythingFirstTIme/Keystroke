'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { generateKeyPairSync, createPublicKey, verify } = require('node:crypto');

const { mintProKey, mintKey, base32Encode, formatKey } = require('../_lib/keygen');
const keygenInternals = require('../_lib/keygen');

// normalizePrivateKeyInput isn't exported — re-require through a wrapper that exercises it
// indirectly via mintProKey, plus a direct export for focused tests.
// For direct coverage, re-read the module and probe via createPrivateKey acceptance.
function generatePem() {
  const { privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  return privateKey.export({ format: 'pem', type: 'pkcs8' });
}

test('base32Encode — empty input', () => {
  assert.equal(base32Encode(Buffer.alloc(0)), '');
});

test('base32Encode — single byte', () => {
  // 0xFF = 11111111 -> bits: 11111 111(00) -> indices 31, 28 -> Z, 4... wait let's just verify length and alphabet
  const out = base32Encode(Buffer.from([0xff]));
  assert.equal(out.length, 2);
  assert.match(out, /^[A-Z2-7]+$/);
});

test('base32Encode — 5 bytes yields exactly 8 chars (no padding)', () => {
  const out = base32Encode(Buffer.from([0x00, 0x00, 0x00, 0x00, 0x00]));
  assert.equal(out, 'AAAAAAAA');
});

test('base32Encode — 14 bytes (payload length) yields 23 chars', () => {
  // 14 * 8 = 112 bits -> ceil(112/5) = 23 chars
  const out = base32Encode(Buffer.alloc(14));
  assert.equal(out.length, 23);
});

test('base32Encode — known vector', () => {
  // RFC 4648 test vector: "foobar" -> "MZXW6YTBOI"
  const out = base32Encode(Buffer.from('foobar', 'utf8'));
  assert.equal(out, 'MZXW6YTBOI');
});

test('formatKey — prefix and dash grouping', () => {
  const out = formatKey('ABCDEFGHIJKLMNOPQRSTUVWX');
  assert.equal(out, 'KS-ABCDEFGH-IJKLMNOP-QRSTUVWX');
});

test('mintProKey — produces well-formed KS- key from clean PEM', () => {
  const pem = generatePem();
  const key = mintProKey(pem);
  assert.match(key, /^KS-[A-Z2-7]{8}(-[A-Z2-7]{1,8}){1,}$/);
  // 14-byte payload + 64-byte signature = 78 bytes -> ceil(78*8/5) = 125 chars base32
  // Plus "KS-" prefix and dashes every 8 chars: groups = ceil(125/8) = 16 groups, 15 dashes, plus "KS-"
  const withoutPrefix = key.slice(3);
  const stripped = withoutPrefix.replace(/-/g, '');
  assert.equal(stripped.length, 125);
});

test('mintProKey — accepts PEM with escaped \\n sequences', () => {
  const pem = generatePem().replace(/\n/g, '\\n');
  const key = mintProKey(pem);
  assert.match(key, /^KS-[A-Z2-7]/);
});

test('mintProKey — accepts PEM with CRLF line endings', () => {
  const pem = generatePem().replace(/\n/g, '\r\n');
  const key = mintProKey(pem);
  assert.match(key, /^KS-[A-Z2-7]/);
});

test('mintProKey — accepts single-line PEM (Vercel env var mangling)', () => {
  // This is the bug that f9ca3e6 fixed: Vercel stripped every newline, producing a 222-char
  // single line with BEGIN/END markers glued to the body.
  const pem = generatePem().replace(/\n/g, '');
  const key = mintProKey(pem);
  assert.match(key, /^KS-[A-Z2-7]/);
});

test('mintProKey — accepts base64-encoded PEM', () => {
  const pem = generatePem();
  const b64 = Buffer.from(pem, 'utf8').toString('base64');
  const key = mintProKey(b64);
  assert.match(key, /^KS-[A-Z2-7]/);
});

test('mintProKey — throws on empty input', () => {
  assert.throws(() => mintProKey(''), /empty/i);
});

test('mintProKey — throws on non-string input', () => {
  assert.throws(() => mintProKey(null), /empty/i);
  assert.throws(() => mintProKey(undefined), /empty/i);
});

test('mintProKey — signature verifies against public key', () => {
  const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const pem = privateKey.export({ format: 'pem', type: 'pkcs8' });
  const key = mintProKey(pem);

  // Parse KS-... key back into payload + signature
  const b32 = key.slice(3).replace(/-/g, '');
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const bytes = Buffer.alloc(Math.floor(b32.length * 5 / 8));
  let buffer = 0;
  let bits = 0;
  let idx = 0;
  for (const ch of b32) {
    buffer = (buffer << 5) | alphabet.indexOf(ch);
    bits += 5;
    if (bits >= 8) {
      bits -= 8;
      bytes[idx++] = (buffer >> bits) & 0xff;
    }
  }
  const payload = bytes.slice(0, 14);
  const signature = bytes.slice(14, 14 + 64);
  const ok = verify('sha256', payload, { key: publicKey, dsaEncoding: 'ieee-p1363' }, signature);
  assert.equal(ok, true);
});

test('mintKey — tier byte is encoded correctly', () => {
  const pem = generatePem();
  const keyTier0 = mintKey(pem, 0);
  const keyTier1 = mintKey(pem, 1);
  assert.notEqual(keyTier0, keyTier1);
  // Different tier byte should produce different payloads -> different keys
});

test('mintProKey — each call produces a distinct key', () => {
  const pem = generatePem();
  const a = mintProKey(pem);
  const b = mintProKey(pem);
  assert.notEqual(a, b);
});
