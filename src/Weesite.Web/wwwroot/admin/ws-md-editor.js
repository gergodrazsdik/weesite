// <ws-md-editor>: enhances the edit page's Markdown textarea (ADR 0005, ADR 0006). Without
// JavaScript the textarea, the Preview button and Save work on their own; this adds a bar with a
// formatting toolbar, the media picker and Write / Split / Preview, a live preview rendered by
// the server, word count and reading time, files dropped or pasted on the text going to the media
// picker,
// autosave of unsaved text in this browser, and keyboard shortcuts.
//
// Markup: <ws-md-editor preview-url="/admin/preview" storage-key="..." [saved]> wrapping the
// label and textarea of a form that has an antiforgery token and a submit button value="save".
// A <ws-media-picker> in the same form moves into the bar.

const ACTIONS = [
  { name: "Bold", glyph: "B", className: "bold", key: "b", before: "**", after: "**", placeholder: "bold text" },
  { name: "Italic", glyph: "I", className: "italic", key: "i", before: "_", after: "_", placeholder: "italic text" },
  { name: "Heading", glyph: "H2", className: "mono", line: "## ", placeholder: "Heading" },
  { name: "Link", icon: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"></path><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"></path>', key: "k", before: "[", after: "](https://)", placeholder: "link text" },
  { name: "Code", glyph: "{ }", className: "mono", before: "`", after: "`", placeholder: "code" },
  { name: "List", icon: '<path d="M9 6h11M9 12h11M9 18h11"></path><circle cx="4.5" cy="6" r="1"></circle><circle cx="4.5" cy="12" r="1"></circle><circle cx="4.5" cy="18" r="1"></circle>', line: "- ", placeholder: "item" },
  { name: "Numbered list", icon: '<path d="M10 6h10M10 12h10M10 18h10"></path><path d="M4 4h1.5v4M4 8h3"></path><path d="M4 14.5a1.5 1.5 0 0 1 3 0c0 1.5-3 2-3 3.5h3"></path>', numbered: true, placeholder: "item" },
  { name: "Task list", icon: '<rect x="3" y="4" width="5" height="5" rx="1"></rect><path d="M4.2 6.6l1.1 1.1 1.8-2"></path><rect x="3" y="14" width="5" height="5" rx="1"></rect><path d="M11 6.5h10M11 16.5h10"></path>', line: "- [ ] ", placeholder: "to do" },
  { name: "Horizontal rule", icon: '<path d="M3 12h18"></path><path d="M8 7h8M8 17h8" opacity=".45"></path>', block: "---" },
  { name: "Table", icon: '<rect x="3" y="5" width="18" height="14" rx="1.5"></rect><path d="M3 10h18M3 14.5h18M10 5v14"></path>', block: "| Column | Column |\n| --- | --- |\n| Text | Text |", select: "Column" },
];

const LAYOUTS = ["write", "split", "preview"];
const LAYOUT_KEY = "weesite:editor-layout";
const WORDS_PER_MINUTE = 220;

const debounce = (fn, ms) => {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
};

// Browser storage can be missing or throw (private windows, blocked site data): it's a
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
    this.addBar();
    this.addPanes();
    this.addFoot();
    this.addPreview();
    this.setLayout(storage.get(LAYOUT_KEY) ?? "split");
    this.setUpAutosave();
    this.setUpDrop();
    this.textarea.addEventListener("keydown", (event) => this.onKey(event));
  }

  addBar() {
    this.bar = document.createElement("div");
    this.bar.className = "md-bar";
    const label = this.querySelector("label");
    label?.classList.add("lbl");

    this.toolbar = document.createElement("div");
    this.toolbar.className = "tools";
    this.toolbar.setAttribute("role", "toolbar");
    this.toolbar.setAttribute("aria-label", "Formatting");
    this.toolbar.setAttribute("aria-controls", this.textarea.id);
    for (const action of ACTIONS) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `tool ${action.className ?? ""}`.trim();
      // The glyph is for the eye; the name, for assistive technology, is visually hidden text.
      const glyph = action.icon
        ? `<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" focusable="false">${action.icon}</svg>`
        : action.glyph;
      button.innerHTML = `<span aria-hidden="true">${glyph}</span><span class="visually-hidden">${action.name}</span>`;
      button.title = action.key ? `${action.name} (Ctrl+${action.key.toUpperCase()})` : action.name;
      if (action.key) {
        button.setAttribute("aria-keyshortcuts", `Control+${action.key.toUpperCase()}`);
      }
      button.addEventListener("click", () => this.apply(action));
      this.toolbar.append(button);
    }

    this.layoutControl = document.createElement("div");
    this.layoutControl.className = "seg-ctl layouts";
    this.layoutControl.setAttribute("role", "group");
    this.layoutControl.setAttribute("aria-label", "Layout");
    for (const layout of LAYOUTS) {
      const button = document.createElement("button");
      button.type = "button";
      button.dataset.layout = layout;
      button.textContent = layout[0].toUpperCase() + layout.slice(1);
      button.addEventListener("click", () => this.setLayout(layout));
      this.layoutControl.append(button);
    }

    this.bar.append(...(label ? [label] : []), this.toolbar);
    const picker = this.form.querySelector("ws-media-picker");
    if (picker) {
      this.bar.append(picker);
    }
    this.bar.append(this.layoutControl);
    this.prepend(this.bar);
  }

  addPanes() {
    this.panes = document.createElement("div");
    this.panes.className = "md-panes";
    this.textarea.classList.add("md-src");
    this.textarea.before(this.panes);
    this.panes.append(this.textarea);
  }

  addFoot() {
    this.foot = document.createElement("div");
    this.foot.className = "md-foot";
    this.stats = document.createElement("span");
    const hint = document.createElement("span");
    hint.textContent = "drop or paste files here to upload";
    this.foot.append(this.stats, hint);
    this.append(this.foot);
    const count = () => {
      const words = this.textarea.value.trim().split(/\s+/u).filter(Boolean).length;
      this.stats.textContent = `markdown · ${words} words · ${Math.max(1, Math.ceil(words / WORDS_PER_MINUTE))} min read`;
    };
    this.textarea.addEventListener("input", count);
    count();
  }

  setLayout(layout) {
    const chosen = LAYOUTS.includes(layout) ? layout : "split";
    this.dataset.layout = chosen;
    for (const button of this.layoutControl.querySelectorAll("button")) {
      button.setAttribute("aria-pressed", String(button.dataset.layout === chosen));
    }
    this.textarea.hidden = chosen === "preview";
    if (this.preview) {
      this.preview.hidden = chosen === "write";
    }
    storage.set(LAYOUT_KEY, chosen);
  }

  // Wraps the selection (or a placeholder) in the action's markers, or prefixes its lines.
  apply(action) {
    const { textarea } = this;
    if (textarea.hidden) {
      this.setLayout("split");
    }
    const { selectionStart: start, selectionEnd: end, value } = textarea;
    const selected = value.slice(start, end) || action.placeholder;
    let text;
    let from;
    let to;
    if (action.block) {
      // A block on lines of its own, with a blank line before and after where there's text.
      const before = value.slice(0, start);
      const after = value.slice(end);
      const lead = before === "" || before.endsWith("\n\n") ? "" : before.endsWith("\n") ? "\n" : "\n\n";
      const trail = after === "" || after.startsWith("\n\n") ? "" : after.startsWith("\n") ? "\n" : "\n\n";
      text = lead + action.block + trail;
      textarea.setRangeText(text, start, end, "end");
      const at = action.select ? text.indexOf(action.select) : text.length - trail.length;
      from = start + at;
      to = start + (action.select ? at + action.select.length : at);
    } else if (action.numbered) {
      const lineStart = value.lastIndexOf("\n", start - 1) + 1;
      const block = value.slice(lineStart, end) || action.placeholder;
      text = block.split("\n").map((line, i) => `${i + 1}. ${line}`).join("\n");
      textarea.setRangeText(text, lineStart, Math.max(end, lineStart), "end");
      from = lineStart + 3;
      to = lineStart + text.length;
    } else if (action.line) {
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
    this.preview.className = "live-preview md-prev content";
    this.preview.setAttribute("aria-label", "Live preview");
    this.panes.append(this.preview);
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

  // Files dropped or pasted on the text (a screenshot, an image copied from elsewhere) open the
  // media picker with the file chosen, to add its description; pasted text is left alone. The
  // picker is looked up when it happens: its script loads after this one, so at setup it isn't
  // ready yet (which once left drops doing nothing).
  setUpDrop() {
    const picker = () => {
      const element = this.form.querySelector("ws-media-picker");
      return element?.openWithFile ? element : null;
    };
    this.panes.addEventListener("dragover", (event) => {
      if (event.dataTransfer?.types.includes("Files") && picker()) {
        event.preventDefault();
        this.panes.classList.add("dropping");
      }
    });
    this.panes.addEventListener("dragleave", () => this.panes.classList.remove("dropping"));
    this.panes.addEventListener("drop", (event) => {
      const file = event.dataTransfer?.files?.[0];
      this.panes.classList.remove("dropping");
      const target = picker();
      if (file && target) {
        event.preventDefault();
        target.openWithFile(file);
      }
    });
    this.textarea.addEventListener("paste", (event) => {
      const file = [...(event.clipboardData?.files ?? [])][0];
      const target = picker();
      if (file && target) {
        event.preventDefault();
        target.openWithFile(file);
      }
    });
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
    this.before(bar);
  }
}

customElements.define("ws-md-editor", WsMdEditor);
