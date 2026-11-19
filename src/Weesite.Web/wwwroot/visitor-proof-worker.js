// The proof of work's solver (ADR 0011), in a worker so typing never stutters: finds the number
// whose SHA-256, after the salt, is the challenge. Hashes run a batch at a time, since each
// crypto.subtle call has a fixed cost that overlapping calls share.

const BATCH = 256;

self.addEventListener("message", async (event) => {
  const { salt, challenge, maxNumber } = event.data ?? {};
  try {
    self.postMessage({ number: await solve(salt, challenge, maxNumber) });
  } catch {
    self.postMessage({ number: null });
  }
});

/**
 * Finds the number, or null when none up to maxNumber matches.
 * @param {string} salt
 * @param {string} challenge lowercase hex
 * @param {number} maxNumber
 * @returns {Promise<number | null>}
 */
async function solve(salt, challenge, maxNumber) {
  const encoder = new TextEncoder();
  const target = new Uint8Array(challenge.match(/../g).map((pair) => parseInt(pair, 16)));
  for (let start = 0; start <= maxNumber; start += BATCH) {
    const end = Math.min(start + BATCH - 1, maxNumber);
    const digests = [];
    for (let number = start; number <= end; number++) {
      digests.push(crypto.subtle.digest("SHA-256", encoder.encode(salt + number)));
    }

    const results = await Promise.all(digests);
    for (let i = 0; i < results.length; i++) {
      if (equal(new Uint8Array(results[i]), target)) {
        return start + i;
      }
    }
  }

  return null;
}

function equal(a, b) {
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
