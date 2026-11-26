// The proof of work's solver (ADR 0011), in a worker so typing never stutters: finds the number
// whose PBKDF2-SHA-256 key, with the challenge's salt and cost, is the challenge.
import { derive, equal, fromHex } from "./visitor-proof-kdf.js";

self.addEventListener("message", async (event) => {
  const { algorithm, salt, challenge, cost, maxNumber } = event.data ?? {};
  try {
    self.postMessage({ number: await solve(algorithm, salt, challenge, cost, maxNumber) });
  } catch {
    self.postMessage({ number: null });
  }
});

/**
 * Finds the number, or null when none up to maxNumber matches. One key at a time: Chromium runs
 * Web Crypto's PBKDF2 on a shared thread, so overlapping calls or more workers don't go faster.
 * @param {string} algorithm
 * @param {string} salt
 * @param {string} challenge lowercase hex
 * @param {number} cost
 * @param {number} maxNumber
 * @returns {Promise<number | null>}
 */
async function solve(algorithm, salt, challenge, cost, maxNumber) {
  if (algorithm !== "PBKDF2/SHA-256") {
    return null;
  }

  const target = fromHex(challenge);
  for (let number = 0; number <= maxNumber; number++) {
    if (equal(await derive(salt, number, cost), target)) {
      return number;
    }
  }

  return null;
}
