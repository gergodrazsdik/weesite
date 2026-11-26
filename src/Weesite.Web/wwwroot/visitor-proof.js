// The invisible proof of work for what visitors send (ADR 0011). The server hands out a challenge
// (/visitor/challenge); a worker finds the number whose PBKDF2-SHA-256 key it names; the answer,
// "number:signature", goes with the form or the request. Nothing to see or solve for the visitor.
// Answers last 10 minutes and work once, so a form asks for a fresh one when its answer is older
// than 9.

import { derive, equal, fromHex } from "./visitor-proof-kdf.js";

const FRESH_FOR = 9 * 60 * 1000;

/**
 * Gets an answer for a purpose: fetches a challenge and solves it.
 * @param {"contact" | "comment" | "reaction"} purpose
 * @returns {Promise<string>} "number:signature"
 */
export async function solve(purpose) {
  const response = await fetch(`/visitor/challenge?for=${encodeURIComponent(purpose)}`, {
    headers: { Accept: "application/json" },
    cache: "no-store",
    credentials: "same-origin",
  });
  if (!response.ok) {
    throw new Error(`No challenge (${response.status})`);
  }

  const challenge = await response.json();
  const number = await work(challenge);
  if (number === null) {
    throw new Error("No answer");
  }

  return `${number}:${challenge.signature}`;
}

/**
 * Keeps an answer ready for a purpose, solving the next one in the background: for buttons such
 * as reactions, where each press needs its own answer.
 */
export class AnswerSupply {
  /** @param {"contact" | "comment" | "reaction"} purpose */
  constructor(purpose) {
    this.purpose = purpose;
    this.pending = null;
  }

  /** Starts solving, if nothing is solved or being solved. */
  prepare() {
    if (!this.pending) {
      const startedAt = Date.now();
      const answer = solve(this.purpose);
      const pending = { answer, startedAt };
      // A failed answer isn't kept: the next take() solves again (F-0216).
      answer.catch(() => {
        if (this.pending === pending) {
          this.pending = null;
        }
      });
      this.pending = pending;
    }
  }

  /**
   * Takes the ready answer (waiting for it if need be) and starts on the next one. When the answer
   * solved ahead fails (the network was down then), it solves once more before giving up.
   * @returns {Promise<string>}
   */
  async take() {
    if (this.pending && Date.now() - this.pending.startedAt > FRESH_FOR) {
      this.pending = null;
    }

    const ahead = this.pending !== null;
    this.prepare();
    const { answer } = this.pending;
    this.pending = null;
    try {
      return await answer;
    } catch (error) {
      if (!ahead) {
        throw error;
      }

      return await solve(this.purpose);
    } finally {
      this.prepare();
    }
  }
}

/**
 * Guards a form: solves a challenge once the visitor starts on it, puts the answer in the form's
 * `proof` field, and, if they send before it's ready, says so in the form's [data-proof-status]
 * (from its data-waiting text) and sends when it is. If no answer comes (no network, an old
 * browser), the form goes without one, and the server decides.
 * @param {HTMLFormElement} form
 * @param {"contact" | "comment"} purpose
 */
export function guard(form, purpose) {
  const field = form.elements.namedItem("proof");
  const status = form.querySelector("[data-proof-status]");
  if (!(field instanceof HTMLInputElement) || form.dataset.proofReady) {
    return;
  }

  form.dataset.proofReady = "true";
  let current = null;
  const start = () => {
    if (!current || Date.now() - current.startedAt > FRESH_FOR) {
      const startedAt = Date.now();
      const answer = solve(purpose).then(
        (value) => {
          field.value = value;
          return value;
        },
        () => null,
      );
      current = { answer, startedAt };
    }

    return current.answer;
  };

  form.addEventListener("focusin", start, { once: true });
  form.addEventListener("input", start, { once: true });

  let sending = false;
  form.addEventListener("submit", async (event) => {
    if (sending) {
      return;
    }

    const stale = current && Date.now() - current.startedAt > FRESH_FOR;
    if (field.value && !stale) {
      return;
    }

    event.preventDefault();
    if (stale) {
      field.value = "";
    }

    if (status) {
      status.textContent = status.dataset.waiting ?? "";
    }

    const submitter = event.submitter;
    await start();
    if (status) {
      status.textContent = "";
    }

    sending = true;
    form.requestSubmit(submitter instanceof HTMLElement ? submitter : undefined);
    sending = false;
  });
}

/**
 * Finds the number in a worker, or on this thread where workers can't run.
 * @param {{ algorithm: string, salt: string, challenge: string, cost: number, maxNumber: number }} challenge
 * @returns {Promise<number | null>}
 */
function work(challenge) {
  const { algorithm, salt, challenge: hash, cost, maxNumber } = challenge;
  if (typeof Worker === "undefined") {
    return solveHere(algorithm, salt, hash, cost, maxNumber);
  }

  return new Promise((resolve) => {
    let worker;
    try {
      worker = new Worker(new URL("./visitor-proof-worker.js", import.meta.url), { type: "module" });
    } catch {
      resolve(solveHere(algorithm, salt, hash, cost, maxNumber));
      return;
    }

    worker.addEventListener("message", (event) => {
      worker.terminate();
      resolve(event.data?.number ?? null);
    });
    worker.addEventListener("error", () => {
      worker.terminate();
      resolve(solveHere(algorithm, salt, hash, cost, maxNumber));
    });
    worker.postMessage({ algorithm, salt, challenge: hash, cost, maxNumber });
  });
}

async function solveHere(algorithm, salt, hash, cost, maxNumber) {
  if (algorithm !== "PBKDF2/SHA-256") {
    return null;
  }

  const target = fromHex(hash);
  for (let number = 0; number <= maxNumber; number++) {
    if (equal(await derive(salt, number, cost), target)) {
      return number;
    }
  }

  return null;
}
