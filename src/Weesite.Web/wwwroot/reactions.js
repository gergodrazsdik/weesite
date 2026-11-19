// <ws-reactions>: reactions to a post (ADR 0011, T-0158). The server renders them hidden; this
// fetches the counts and shows the reactions given, as emoji with their counts, and a React button
// that opens the four to choose from. A visitor has one reaction per post: choosing another
// changes it, choosing it again takes it back. Each choice goes with a proof of work, solved ahead
// in the background once the reactions scroll into view; it shows at once and is put right if the
// server refuses. The browser remembers its reaction in localStorage ("weesite:reactions": the
// post's source path to a one-item list, the shape earlier versions kept several in).
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

const writeGiven = (key, kind) => {
  try {
    const all = readGiven();
    if (kind) {
      all[key] = [kind];
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
    const tallies = this.querySelector(".reaction-tallies");
    const items = [...this.querySelectorAll(".reaction-tallies li[data-kind]")];
    const chooser = this.querySelector(".reaction-chooser");
    const toggle = this.querySelector(".reaction-toggle");
    const panel = this.querySelector(".reaction-options");
    const options = [...this.querySelectorAll(".reaction-option[data-kind]")];
    const status = this.querySelector(".reactions-status");
    const announce = this.querySelector(".reactions-announce");
    if (!toggle || !panel || options.length === 0) {
      return;
    }

    const say = (text) => {
      if (status) {
        status.textContent = text ?? "";
      }
    };
    const tell = (text) => {
      if (announce) {
        announce.textContent = text ?? "";
      }
    };
    const option = (kind) => options.find((o) => o.dataset.kind === kind);
    const fill = (template, value) => (template ?? "").replace("{0}", value ?? "");

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

    let chosen = (readGiven()[key] ?? []).find((kind) => option(kind)) ?? null;

    const icon = toggle.querySelector(".reaction-toggle-icon");
    const emoji = toggle.querySelector(".reaction-toggle-emoji");
    const text = toggle.querySelector(".reaction-toggle-text");
    const render = () => {
      let any = false;
      for (const item of items) {
        const count = Math.max(0, counts[item.dataset.kind] ?? 0);
        item.hidden = count === 0;
        any ||= count > 0;
        const number = item.querySelector(".reaction-count");
        if (number) {
          number.textContent = String(count);
        }
      }
      if (tallies) {
        tallies.hidden = !any;
      }

      for (const o of options) {
        o.setAttribute("aria-pressed", String(o.dataset.kind === chosen));
      }

      // Without a reaction the button says React; with one it shows its emoji, and its name for
      // screen readers and as a tooltip.
      const mine = chosen ? option(chosen) : null;
      // An SVG element has no hidden property: the attribute it is.
      icon?.toggleAttribute("hidden", Boolean(mine));
      if (emoji) {
        emoji.hidden = !mine;
        emoji.textContent = mine?.dataset.emoji ?? "";
      }
      if (text) {
        text.textContent = mine ? fill(toggle.dataset.yours, mine.dataset.label) : (toggle.dataset.react ?? "");
        text.classList.toggle("visually-hidden", Boolean(mine));
      }
      toggle.classList.toggle("chosen", Boolean(mine));
      if (mine) {
        toggle.title = fill(toggle.dataset.yours, mine.dataset.label);
      } else {
        toggle.removeAttribute("title");
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

    // The choices open under the button, like a disclosure: Escape, a click elsewhere or the focus
    // leaving close them; the arrow keys move between them.
    const isOpen = () => !panel.hidden;
    const open = () => {
      panel.hidden = false;
      toggle.setAttribute("aria-expanded", "true");
      (option(chosen) ?? options[0]).focus();
    };
    const close = (focusToggle) => {
      if (!isOpen()) {
        return;
      }
      panel.hidden = true;
      toggle.setAttribute("aria-expanded", "false");
      if (focusToggle) {
        toggle.focus();
      }
    };

    toggle.addEventListener("click", () => (isOpen() ? close(false) : open()));
    chooser?.addEventListener("keydown", (event) => {
      if (event.key === "Escape" && isOpen()) {
        event.preventDefault();
        close(true);
        return;
      }
      const at = options.indexOf(document.activeElement);
      if (at < 0) {
        return;
      }
      const next = {
        ArrowRight: (at + 1) % options.length,
        ArrowDown: (at + 1) % options.length,
        ArrowLeft: (at - 1 + options.length) % options.length,
        ArrowUp: (at - 1 + options.length) % options.length,
        Home: 0,
        End: options.length - 1,
      }[event.key];
      if (next !== undefined) {
        event.preventDefault();
        options[next].focus();
      }
    });
    chooser?.addEventListener("focusout", (event) => {
      if (!chooser.contains(event.relatedTarget)) {
        close(false);
      }
    });
    document.addEventListener("pointerdown", (event) => {
      if (isOpen() && !chooser?.contains(event.target)) {
        close(false);
      }
    });

    let busy = false;
    for (const o of options) {
      o.addEventListener("click", async () => {
        if (busy) {
          return;
        }
        busy = true;
        const kind = o.dataset.kind;
        const previous = chosen;
        const undo = previous === kind;
        const before = { ...counts };

        // Shown at once; the server's counts replace these when they come.
        const add = (k, delta) => {
          counts = { ...counts, [k]: Math.max(0, (counts[k] ?? 0) + delta) };
        };
        if (previous) {
          add(previous, -1);
        }
        if (!undo) {
          add(kind, 1);
        }
        chosen = undo ? null : kind;
        render();
        say("");
        close(true);

        try {
          const proof = await supply.take();
          const response = await fetch("/visitor/reactions", {
            method: "POST",
            headers: { "Content-Type": "application/json", Accept: "application/json" },
            body: JSON.stringify({ post, kind, undo, replaces: undo ? null : previous, proof }),
          });
          if (response.ok) {
            counts = (await response.json()).counts ?? counts;
            writeGiven(key, chosen);
            render();
            tell(chosen ? fill(this.dataset.chosen, o.dataset.label) : this.dataset.removed);
          } else {
            counts = before;
            chosen = previous;
            render();
            say(response.status === 429 ? this.dataset.limited : this.dataset.failed);
          }
        } catch {
          counts = before;
          chosen = previous;
          render();
          say(this.dataset.failed);
        } finally {
          busy = false;
        }
      });
    }
  }
}

if (!customElements.get("ws-reactions")) {
  customElements.define("ws-reactions", WsReactions);
}
