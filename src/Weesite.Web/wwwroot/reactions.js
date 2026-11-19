// <ws-reactions>: reactions to a post (ADR 0011). The server renders the buttons hidden; this
// fetches the counts, shows them, and sends each press with a proof of work, solved ahead in the
// background once the buttons scroll into view. A press shows at once and is put right if the
// server refuses. The browser remembers what it gave in localStorage ("weesite:reactions": the
// post's source path to the reactions given); pressing a given one takes it back.
// Texts come from the element's data attributes, written from the site's resources.

import { AnswerSupply } from "./visitor-proof.js";

const STORE = "weesite:reactions";

const readGiven = () => {
  try {
    const parsed = JSON.parse(localStorage.getItem(STORE) ?? "{}");
    return parsed && typeof parsed === "object" ? parsed : {};
  } catch {
    return {};
  }
};

const writeGiven = (key, kinds) => {
  try {
    const all = readGiven();
    if (kinds.length > 0) {
      all[key] = kinds;
    } else {
      delete all[key];
    }
    localStorage.setItem(STORE, JSON.stringify(all));
  } catch {
    // Storage refused (private mode, full): the reaction still counts, only this browser forgets.
  }
};

class WsReactions extends HTMLElement {
  async connectedCallback() {
    if (this.dataset.ready) {
      return;
    }
    this.dataset.ready = "true";

    const post = this.dataset.post ?? "";
    const key = this.dataset.key ?? post;
    const buttons = [...this.querySelectorAll("button[data-kind]")];
    const status = this.querySelector(".reactions-status");
    const say = (text) => {
      if (status) {
        status.textContent = text ?? "";
      }
    };

    let counts;
    try {
      const response = await fetch(`/visitor/reactions?post=${encodeURIComponent(post)}`, {
        headers: { Accept: "application/json" },
        cache: "no-store",
      });
      if (!response.ok) {
        return;
      }
      counts = (await response.json()).counts ?? {};
    } catch {
      return;
    }

    const given = new Set((readGiven()[key] ?? []).filter((kind) => buttons.some((b) => b.dataset.kind === kind)));
    const render = () => {
      for (const button of buttons) {
        const kind = button.dataset.kind;
        button.setAttribute("aria-pressed", String(given.has(kind)));
        const count = button.querySelector(".reaction-count");
        if (count) {
          count.textContent = String(Math.max(0, counts[kind] ?? 0));
        }
      }
    };
    render();
    this.hidden = false;

    const supply = new AnswerSupply("reaction");
    if ("IntersectionObserver" in window) {
      const observer = new IntersectionObserver((entries) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          supply.prepare();
          observer.disconnect();
        }
      });
      observer.observe(this);
    } else {
      supply.prepare();
    }

    for (const button of buttons) {
      button.addEventListener("click", async () => {
        if (button.getAttribute("aria-disabled") === "true") {
          return;
        }
        const kind = button.dataset.kind;
        const undo = given.has(kind);
        const before = { counts: { ...counts }, given: new Set(given) };

        // Shown at once; the server's counts replace these when they come.
        if (undo) {
          given.delete(kind);
        } else {
          given.add(kind);
        }
        counts = { ...counts, [kind]: Math.max(0, (counts[kind] ?? 0) + (undo ? -1 : 1)) };
        render();
        say("");
        button.setAttribute("aria-disabled", "true");

        const restore = (message) => {
          counts = before.counts;
          given.clear();
          before.given.forEach((k) => given.add(k));
          render();
          say(message);
        };

        try {
          const proof = await supply.take();
          const response = await fetch("/visitor/reactions", {
            method: "POST",
            headers: { "Content-Type": "application/json", Accept: "application/json" },
            body: JSON.stringify({ post, kind, undo, proof }),
          });
          if (response.ok) {
            counts = (await response.json()).counts ?? counts;
            writeGiven(key, [...given]);
            render();
          } else {
            restore(response.status === 429 ? this.dataset.limited : this.dataset.failed);
          }
        } catch {
          restore(this.dataset.failed);
        } finally {
          button.removeAttribute("aria-disabled");
        }
      });
    }
  }
}

if (!customElements.get("ws-reactions")) {
  customElements.define("ws-reactions", WsReactions);
}
