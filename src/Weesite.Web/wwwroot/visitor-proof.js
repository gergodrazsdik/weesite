// The invisible proof of work for what visitors send (ADR 0011). The server hands out a challenge
// (/visitor/challenge); a worker finds its number; the answer, "number:signature", goes with the
// form or the request. Nothing to see or solve for the visitor. Answers last 10 minutes and work
// once, so a form asks for a fresh one when its answer is older than 9.

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
 * @param {{ salt: string, challenge: string, maxNumber: number }} challenge
 * @returns {Promise<number | null>}
 */
function work(challenge) {
  const { salt, challenge: hash, maxNumber } = challenge;
  if (typeof Worker === "undefined") {
    return solveHere(salt, hash, maxNumber);
  }

  return new Promise((resolve) => {
    let worker;
    try {
      worker = new Worker(new URL("./visitor-proof-worker.js", import.meta.url));
    } catch {
      resolve(solveHere(salt, hash, maxNumber));
      return;
    }

    worker.addEventListener("message", (event) => {
      worker.terminate();
      resolve(event.data?.number ?? null);
    });
    worker.addEventListener("error", () => {
      worker.terminate();
      resolve(solveHere(salt, hash, maxNumber));
    });
    worker.postMessage({ salt, challenge: hash, maxNumber });
  });
}

async function solveHere(salt, hash, maxNumber) {
  const encoder = new TextEncoder();
  for (let number = 0; number <= maxNumber; number++) {
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", encoder.encode(salt + number)));
    const hex = Array.from(digest, (b) => b.toString(16).padStart(2, "0")).join("");
    if (hex === hash) {
      return number;
    }
  }

  return null;
}
