// The proof of work's key derivation (ADR 0011, T-0188), shared by the worker and the fallback on
// the page: PBKDF2-SHA-256 of the number, as decimal text, with the challenge's salt and cost.

const encoder = new TextEncoder();

/**
 * Derives a number's 32-byte key.
 * @param {string} salt
 * @param {number} number
 * @param {number} cost PBKDF2's iterations
 * @returns {Promise<Uint8Array>}
 */
export async function derive(salt, number, cost) {
  const key = await crypto.subtle.importKey("raw", encoder.encode(String(number)), "PBKDF2", false, ["deriveBits"]);
  const bits = await crypto.subtle.deriveBits({ name: "PBKDF2", hash: "SHA-256", salt: encoder.encode(salt), iterations: cost }, key, 256);
  return new Uint8Array(bits);
}

/**
 * @param {string} hex lowercase hex
 * @returns {Uint8Array}
 */
export function fromHex(hex) {
  return new Uint8Array((hex.match(/../g) ?? []).map((pair) => parseInt(pair, 16)));
}

/**
 * @param {Uint8Array} a
 * @param {Uint8Array} b
 * @returns {boolean}
 */
export function equal(a, b) {
  if (a.length !== b.length) {
    return false;
  }

  for (let i = 0; i < a.length; i++) {
    if (a[i] !== b[i]) {
      return false;
    }
  }

  return true;
}
