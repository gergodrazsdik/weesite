// <ws-md-editor>: enhances the edit page's Markdown textarea (ADR 0005). Without JavaScript the
// textarea, the Preview button and Save work on their own; this adds a toolbar, a live preview
// rendered by the server, autosave of unsaved text in this browser, and keyboard shortcuts.
//
// Markup: <ws-md-editor preview-url="/admin/preview" storage-key="..." [saved]> wrapping the
// label and textarea of a form that has an antiforgery token and a submit button value="save".

const ACTIONS = [
  { name: "Bold", key: "b", before: "**", after: "**", placeholder: "bold text" },
  { name: "Italic", key: "i", before: "_", after: "_", placeholder: "italic text" },
  { name: "Heading", line: "## ", placeholder: "Heading" },
  { name: "Link", key: "k", before: "[", after: "](https://)", placeholder: "link text" },
  { name: "Code", before: "`", after: "`", placeholder: "code" },
  { name: "List", line: "- ", placeholder: "item" },
];

const debounce = (fn, ms) => {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
};

// Browser storage can be missing or throw (private windows, blocked site data): autosave is a
// convenience, so it just stops working then.
const storage = {
  get(key) {
    try {
      return JSON.parse(localStorage.getItem(key) ?? "null");
    } catch {
      return null;
    }
  },
  set(key, value) {
    try {
      localStorage.setItem(key, JSON.stringify(value));
    } catch {
      // ignore
    }
  },
  remove(key) {
    try {
      localStorage.removeItem(key);
    } catch {
      // ignore
    }
  },
};

class WsMdEditor extends HTMLElement {
  connectedCallback() {
    this.textarea = this.querySelector("textarea");
    this.form = this.textarea?.form;
    if (!this.textarea || !this.form || this.toolbar) {
      return;
    }

    this.key = this.getAttribute("storage-key");
    this.addToolbar();
    this.addPreview();
    this.setUpAutosave();
    this.textarea.addEventListener("keydown", (event) => this.onKey(event));
  }

  addToolbar() {
    this.toolbar = document.createElement("div");
    this.toolbar.className = "md-toolbar";
    this.toolbar.setAttribute("role", "toolbar");
    this.toolbar.setAttribute("aria-label", "Formatting");
    this.toolbar.setAttribute("aria-controls", this.textarea.id);
    for (const action of ACTIONS) {
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = action.name;
      if (action.key) {
        button.setAttribute("aria-keyshortcuts", `Control+${action.key.toUpperCase()}`);
      }
      button.addEventListener("click", () => this.apply(action));
      this.toolbar.append(button);
    }
    this.textarea.before(this.toolbar);
  }

  // Wraps the selection (or a placeholder) in the action's markers, or prefixes its lines.
  apply(action) {
    const { textarea } = this;
    const { selectionStart: start, selectionEnd: end, value } = textarea;
    const selected = value.slice(start, end) || action.placeholder;
    let text;
    let from;
    let to;
    if (action.line) {
      const lineStart = value.lastIndexOf("\n", start - 1) + 1;
      const block = value.slice(lineStart, end) || action.placeholder;
      text = block.split("\n").map((line) => action.line + line).join("\n");
      textarea.setRangeText(text, lineStart, Math.max(end, lineStart), "end");
      from = lineStart + action.line.length;
      to = lineStart + text.length;
    } else {
      text = action.before + selected + action.after;
      textarea.setRangeText(text, start, end, "end");
      from = start + action.before.length;
      to = from + selected.length;
    }
    textarea.focus();
    textarea.setSelectionRange(from, to);
    textarea.dispatchEvent(new Event("input", { bubbles: true }));
  }

  onKey(event) {
    if (!(event.ctrlKey || event.metaKey) || event.altKey) {
      return;
    }
    const key = event.key.toLowerCase();
    if (key === "s") {
      event.preventDefault();
      this.form.querySelector('button[type="submit"][value="save"]')?.click();
      return;
    }
    const action = ACTIONS.find((a) => a.key === key);
    if (action) {
      event.preventDefault();
      this.apply(action);
    }
  }

  addPreview() {
    const url = this.getAttribute("preview-url");
    if (!url) {
      return;
    }
    this.preview = document.createElement("section");
    this.preview.className = "live-preview content";
    this.preview.setAttribute("aria-label", "Live preview");
    this.append(this.preview);
    this.classList.add("with-preview");

    const render = debounce(async () => {
      const body = new FormData();
      body.set("markdown", this.textarea.value);
      body.set("__RequestVerificationToken", this.form.querySelector('input[name="__RequestVerificationToken"]')?.value ?? "");
      try {
        const response = await fetch(url, { method: "POST", body, credentials: "same-origin" });
        if (response.ok) {
          // Server-rendered and sanitized by the same renderer as the public site.
          this.preview.innerHTML = await response.text();
        }
      } catch {
        // Offline or signed out: the preview just stops updating.
      }
    }, 400);
    this.textarea.addEventListener("input", render);
    render();
  }

  // Keeps unsaved text in this browser and offers it back after a crash or a closed tab.
  setUpAutosave() {
    if (!this.key) {
      return;
    }
    if (this.hasAttribute("saved")) {
      storage.remove(this.key);
    }

    const etag = this.form.querySelector('input[name="Form.ETag"]')?.value ?? "";
    const stored = storage.get(this.key);
    if (stored && stored.etag === etag && stored.text !== this.textarea.value) {
      this.offerRestore(stored);
    }

    const save = debounce(() => storage.set(this.key, { text: this.textarea.value, etag, at: new Date().toISOString() }), 800);
    this.textarea.addEventListener("input", save);
  }

  offerRestore(stored) {
    const bar = document.createElement("div");
    bar.className = "notice restore";
    bar.setAttribute("role", "status");
    const message = document.createElement("span");
    message.textContent = `Unsaved changes from ${new Date(stored.at).toLocaleString()}. `;
    const restore = document.createElement("button");
    restore.type = "button";
    restore.textContent = "Restore";
    restore.addEventListener("click", () => {
      this.textarea.value = stored.text;
      this.textarea.dispatchEvent(new Event("input", { bubbles: true }));
      bar.remove();
      this.textarea.focus();
    });
    const discard = document.createElement("button");
    discard.type = "button";
    discard.textContent = "Discard";
    discard.addEventListener("click", () => {
      storage.remove(this.key);
      bar.remove();
    });
    bar.append(message, restore, " ", discard);
    this.prepend(bar);
  }
}

customElements.define("ws-md-editor", WsMdEditor);
