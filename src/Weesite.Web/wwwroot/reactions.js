// <ws-reactions>: reactions to a post (ADR 0011, T-0158). The server renders them hidden; this
// fetches the counts and shows the reactions given, as emoji with their counts, and a React button
// that opens the five to choose from. A visitor has one reaction per post: choosing another
// changes it, choosing it again takes it back. Each choice goes with a proof of work, solved ahead
// in the background once the reactions scroll into view; it shows at once and is put right if the
// server refuses. The browser remembers its reaction in localStorage ("weesite:reactions": the
// post's source path to the reaction and the server's day it was given). The server changes or takes
// back a reaction only on the day it was given (ADR 0011), so one from an earlier day isn't shown
// as the visitor's: choosing then gives a new one, which the page doesn't pretend replaces it (U-13).
// Earlier versions kept a list without the day, which counts as an earlier day.
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

// The reaction kept for a post and its day; null without one, and no day in the old list shape.
const givenFor = (key) => {
  const entry = readGiven()[key];
  if (Array.isArray(entry)) {
    return typeof entry[0] === "string" ? { kind: entry[0], day: null } : null;
  }
  return entry && typeof entry.kind === "string" ? { kind: entry.kind, day: typeof entry.day === "string" ? entry.day : null } : null;
};

const writeGiven = (key, kind, day) => {
  try {
    const all = readGiven();
    if (kind) {
      all[key] = { kind, day };
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

    // The server's day (UTC), from its answers: what this browser gave is the visitor's to change
    // only on that day. Should the page stay open past midnight (UTC), the day has moved on.
    let day = null;
    let dayRead = 0;
    const utcDay = (at) => new Date(at).toISOString().slice(0, 10);
    const today = () => (day && utcDay(dayRead) === utcDay(Date.now()) ? day : null);
    const learnDay = (answer) => {
      if (typeof answer?.day === "string") {
        day = answer.day;
        dayRead = Date.now();
      }
    };

    // The post's counts as the server has them; null when they can't be read.
    const fresh = async () => {
      try {
        const response = await fetch(`/visitor/reactions?post=${encodeURIComponent(post)}`, {
          headers: { Accept: "application/json" },
          cache: "no-store",
        });
        if (!response.ok) {
          return null;
        }
        const answer = await response.json();
        learnDay(answer);
        return answer.counts ?? {};
      } catch {
        return null;
      }
    };

    let counts = await fresh();
    if (!counts) {
      return;
    }

    // Today's reaction from this browser; one from an earlier day is forgotten (U-13).
    const kept = givenFor(key);
    let chosen = kept && option(kept.kind) && kept.day && kept.day === today() ? kept.kind : null;
    if (kept && !chosen) {
      writeGiven(key, null);
    }

    const icon = toggle.querySelector(".reaction-toggle-icon");
    const emoji = toggle.querySelector(".reaction-toggle-emoji");
    const number = toggle.querySelector(".reaction-toggle-count");
    const text = toggle.querySelector(".reaction-toggle-text");
    const summary = this.querySelector(".reaction-summary");
    const box = this.querySelector(".reaction-summary-box");
    const closeIcon = this.querySelector(".reaction-summary-close");
    const summaryText = this.querySelector(".reaction-summary-text");
    const stack = this.querySelector(".reaction-stack");
    const lang = document.documentElement.lang || undefined;
    const totalText = (total) =>
      total === 0 ? this.dataset.totalNone : total === 1 ? this.dataset.totalOne : fill(this.dataset.totalMany, total.toLocaleString(lang));
    // Open, the box shows each reaction's count in place of the stack, and an × closes it.
    function showCounts(open) {
      summary?.setAttribute("aria-expanded", String(open));
      if (tallies) {
        tallies.hidden = !open;
      }
      if (stack) {
        stack.hidden = open;
      }
      closeIcon?.toggleAttribute("hidden", !open);
      const label = open ? summary?.dataset.hide : summary?.dataset.show;
      if (summary && label) {
        summary.title = label;
      }
      if (summaryText && label) {
        summaryText.textContent = label;
      }
    }

    const render = () => {
      // The reactions given, most first: their counts, and the three most used stacked (T-0162).
      const given = items
        .map((item) => ({ item, count: Math.max(0, counts[item.dataset.kind] ?? 0) }))
        .sort((a, b) => b.count - a.count);
      for (const { item, count } of given) {
        item.hidden = count === 0;
        const tally = item.querySelector(".reaction-count");
        if (tally) {
          tally.textContent = count.toLocaleString(lang);
        }
        tallies?.append(item);
      }
      const used = given.filter(({ count }) => count > 0);
      const total = used.reduce((sum, { count }) => sum + count, 0);
      if (box) {
        box.hidden = total === 0;
      }
      if (total === 0) {
        showCounts(false);
      }
      if (stack) {
        stack.replaceChildren(...used.slice(0, 3).map(({ item }) => {
          const one = document.createElement("span");
          one.textContent = item.dataset.emoji ?? "";
          return one;
        }));
      }

      for (const o of options) {
        o.setAttribute("aria-pressed", String(o.dataset.kind === chosen));
      }

      // React shows the visitor's reaction (or a smiley) and the total; its name says both.
      const mine = chosen ? option(chosen) : null;
      icon?.toggleAttribute("hidden", Boolean(mine));
      if (emoji) {
        emoji.hidden = !mine;
        emoji.textContent = mine?.dataset.emoji ?? "";
      }
      if (number) {
        number.hidden = total === 0;
        number.textContent = total.toLocaleString(lang);
      }
      if (text) {
        const name = mine ? fill(toggle.dataset.yours, mine.dataset.label) : (toggle.dataset.react ?? "");
        text.textContent = total === 0 ? name : `${name} (${totalText(total)})`;
        text.classList.toggle("visually-hidden", Boolean(mine) || total > 0);
      }
      toggle.classList.toggle("chosen", Boolean(mine));
      if (mine) {
        toggle.title = fill(toggle.dataset.yours, mine.dataset.label);
      } else {
        toggle.removeAttribute("title");
      }
    };
    summary?.addEventListener("click", () => showCounts(summary.getAttribute("aria-expanded") !== "true"));
    // Escape closes the counts and puts the focus back on their button, as the choices and the
    // Share dialog do (U-18).
    box?.addEventListener("keydown", (event) => {
      if (event.key === "Escape" && summary?.getAttribute("aria-expanded") === "true") {
        event.preventDefault();
        showCounts(false);
        summary.focus();
      }
    });
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
        if (chosen && !today()) {
          // Given on a day that has passed: the server can't change it any more, so this one is new.
          chosen = null;
          writeGiven(key, null);
        }
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
            const answer = await response.json();
            learnDay(answer);
            counts = answer.counts ?? counts;
            writeGiven(key, chosen, day);
            render();
            tell(chosen ? fill(this.dataset.chosen, o.dataset.label) : this.dataset.removed);
          } else {
            // A change can fail after the reaction it replaces was taken back, which stays taken
            // back (F-0250): then this browser forgets it too, and shows the counts as they are.
            const failure = await response.json().catch(() => null);
            if (previous && failure?.takenBack === previous) {
              chosen = null;
              writeGiven(key, null);
              counts = (await fresh()) ?? before;
            } else {
              counts = before;
              chosen = previous;
            }
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
