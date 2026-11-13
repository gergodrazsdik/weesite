// <ws-md-editor>: enhances the edit page's Markdown textarea (ADR 0005, ADR 0006). Without
// JavaScript the textarea and Save work on their own; this adds a bar with a
// formatting toolbar, the media picker and Write / Split / Preview, a live preview rendered by
// the server, word count and reading time, files dropped or pasted on the text going to the media
// picker,
// autosave of unsaved text in this browser, and keyboard shortcuts.
//
// Markup: <ws-md-editor preview-url="/admin/preview" storage-key="..." [saved] [created-key="..."]>
// wrapping the label and textarea of a form that has an antiforgery token and a submit button
// value="save".
// A <ws-media-picker> in the same form moves into the bar.

const ACTIONS = [
  { name: "Bold", glyph: "B", className: "bold", key: "b", before: "**", after: "**", placeholder: "bold text" },
  { name: "Italic", glyph: "I", className: "italic", key: "i", before: "_", after: "_", placeholder: "italic text" },
  { name: "Heading", glyph: "H2", className: "mono", line: "## ", placeholder: "Heading" },
  { name: "Link", icon: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"></path><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"></path>', key: "k", before: "[", after: "](https://)", placeholder: "link text", selectAfter: "https://" },
  { name: "Code", glyph: "{ }", className: "mono", before: "`", after: "`", placeholder: "code" },
  { name: "Code block", icon: '<path d="M8 8l-4 4 4 4M16 8l4 4-4 4"></path><path d="M13.5 6l-3 12"></path>', dialog: "code" },
  { name: "List", icon: '<path d="M9 6h11M9 12h11M9 18h11"></path><circle cx="4.5" cy="6" r="1"></circle><circle cx="4.5" cy="12" r="1"></circle><circle cx="4.5" cy="18" r="1"></circle>', line: "- ", placeholder: "item" },
  { name: "Numbered list", icon: '<path d="M10 6h10M10 12h10M10 18h10"></path><path d="M4 4h1.5v4M4 8h3"></path><path d="M4 14.5a1.5 1.5 0 0 1 3 0c0 1.5-3 2-3 3.5h3"></path>', numbered: true, placeholder: "item" },
  { name: "Task list", icon: '<rect x="3" y="4" width="5" height="5" rx="1"></rect><path d="M4.2 6.6l1.1 1.1 1.8-2"></path><rect x="3" y="14" width="5" height="5" rx="1"></rect><path d="M11 6.5h10M11 16.5h10"></path>', line: "- [ ] ", placeholder: "to do" },
  { name: "Horizontal rule", icon: '<path d="M3 12h18"></path><path d="M8 7h8M8 17h8" opacity=".45"></path>', block: "---" },
  { name: "Table", icon: '<rect x="3" y="5" width="18" height="14" rx="1.5"></rect><path d="M3 10h18M3 14.5h18M10 5v14"></path>', dialog: "table" },
];

const LAYOUTS = ["write", "split", "preview"];
const KEPT_FIELDS = ["Form.Title", "Form.Summary", "Form.Tags"];
const LAYOUT_KEY = "weesite:editor-layout";
const WORDS_PER_MINUTE = 220;

const debounce = (fn, ms) => {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
};

// A time as the editor shows the others (saved at, versions): 2026-11-10 16:33 UTC (F-0050).
const stamp = (iso) => {
  const at = new Date(iso);
  return Number.isNaN(at.getTime()) ? "earlier" : `${at.toISOString().slice(0, 16).replace("T", " ")} UTC`;
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
    for (const [index, action] of ACTIONS.entries()) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `tool ${action.className ?? ""}`.trim();
      // One tab stop for the whole toolbar; the arrow keys move along it (F-0091 L27).
      button.tabIndex = index === 0 ? 0 : -1;
      // The glyph is for the eye; the name, for assistive technology, is visually hidden text.
      const glyph = action.icon
        ? `<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" focusable="false">${action.icon}</svg>`
        : action.glyph;
      button.innerHTML = `<span aria-hidden="true">${glyph}</span><span class="visually-hidden">${action.name}</span>`;
      button.title = action.key ? `${action.name} (Ctrl+${action.key.toUpperCase()})` : action.name;
      if (action.key) {
        button.setAttribute("aria-keyshortcuts", `Control+${action.key.toUpperCase()}`);
      }
      button.addEventListener("click", () => {
        if (action.dialog === "code") {
          this.openCodeDialog(button);
        } else if (action.dialog === "table") {
          this.openTableDialog(button);
        } else {
          this.apply(action);
        }
      });
      this.toolbar.append(button);
    }
    this.toolbar.addEventListener("keydown", (event) => this.onToolbarKey(event));

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
    // Its own picker: the page can have another, for the social card's image.
    const picker = this.form.querySelector(`ws-media-picker[for="${this.textarea.id}"]`);
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

  // The toolbar pattern: Left and Right move to the next button, Home and End to the ends; the
  // button left last is the one Tab comes back to.
  onToolbarKey(event) {
    const buttons = [...this.toolbar.querySelectorAll("button")];
    const current = buttons.indexOf(document.activeElement);
    if (current < 0) {
      return;
    }
    const next = { ArrowRight: current + 1, ArrowLeft: current - 1, Home: 0, End: buttons.length - 1 }[event.key];
    if (next === undefined) {
      return;
    }
    event.preventDefault();
    const target = buttons[(next + buttons.length) % buttons.length];
    for (const button of buttons) {
      button.tabIndex = button === target ? 0 : -1;
    }
    target.focus();
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
      // A function block wraps the selection, such as code into a fence.
      const block = typeof action.block === "function" ? action.block(value.slice(start, end)) : action.block;
      text = lead + block + trail;
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
    } else if (action.before !== "[" && this.unwrap(action, start, end)) {
      // Bold, italic and code toggle: pressed again on marked text, they take the marks off (F-0091 L3).
      return;
    } else {
      text = action.before + selected + action.after;
      textarea.setRangeText(text, start, end, "end");
      from = start + action.before.length;
      to = from + selected.length;
      if (action.selectAfter && start !== end) {
        // A link around chosen text: the address is what's left to type.
        from = start + action.before.length + selected.length + action.after.indexOf(action.selectAfter);
        to = from + action.selectAfter.length;
      }
    }
    textarea.focus();
    textarea.setSelectionRange(from, to);
    textarea.dispatchEvent(new Event("input", { bubbles: true }));
  }

  // Takes the action's marks off the selection when they're around it (outside or inside), as a
  // second press of Bold does; returns whether it did.
  unwrap(action, start, end) {
    const { textarea } = this;
    const { value } = textarea;
    const { before, after } = action;
    const selected = value.slice(start, end);
    let from;
    let to;
    let inner;
    if (value.slice(start - before.length, start) === before && value.slice(end, end + after.length) === after) {
      [from, to, inner] = [start - before.length, end + after.length, selected];
    } else if (selected.length >= before.length + after.length && selected.startsWith(before) && selected.endsWith(after)) {
      [from, to, inner] = [start, end, selected.slice(before.length, selected.length - after.length)];
    } else {
      return false;
    }
    // "**" around "*x*" isn't italic's "_", but "*" inside "**" would be: only exact marks count.
    if (before === "_" && (value[from - 1] === "_" || value[to] === "_")) {
      return false;
    }
    textarea.setRangeText(inner, from, to, "end");
    textarea.focus();
    textarea.setSelectionRange(from, from + inner.length);
    textarea.dispatchEvent(new Event("input", { bubbles: true }));
    return true;
  }

  // The Table dialog (F-0100): how many columns and rows, then a table of that size with the
  // first heading selected, ready to type over.
  openTableDialog(opener) {
    if (!this.tableDialog) {
      this.tableDialog = this.buildTableDialog();
    }
    const { dialog, columns } = this.tableDialog;
    this.tableOpener = opener;
    dialog.showModal();
    columns.select();
  }

  buildTableDialog() {
    const id = `${this.textarea.id}-table`;
    const dialog = document.createElement("dialog");
    dialog.className = "code-dialog table-dialog";
    dialog.setAttribute("aria-labelledby", `${id}-title`);
    const form = document.createElement("form");
    form.method = "dialog";
    form.innerHTML = `
      <h2 id="${id}-title">Insert a table</h2>
      <div class="table-size">
        <div><label for="${id}-columns">Columns</label><input id="${id}-columns" type="number" min="1" max="10" value="3" required></div>
        <div><label for="${id}-rows">Rows</label><input id="${id}-rows" type="number" min="1" max="50" value="3" required aria-describedby="${id}-help"></div>
      </div>
      <p id="${id}-help" class="help">Rows under the heading row.</p>
      <div class="dialog-actions">
        <button type="button" class="btn cancel">Cancel</button>
        <button type="submit" class="btn primary">Insert</button>
      </div>`;
    dialog.append(form);
    this.append(dialog);
    const columns = form.querySelector(`#${id}-columns`);
    const rows = form.querySelector(`#${id}-rows`);
    form.querySelector(".cancel").addEventListener("click", () => dialog.close());
    form.addEventListener("submit", (event) => {
      event.preventDefault();
      if (!form.reportValidity()) {
        return;
      }
      this.inserted = true;
      dialog.close();
      this.insertTable(Number(columns.value), Number(rows.value));
    });
    dialog.addEventListener("close", () => {
      if (!this.inserted) {
        this.tableOpener?.focus();
      }
      this.inserted = false;
    });
    return { dialog, columns, rows };
  }

  insertTable(columns, rows) {
    const line = (cells) => `| ${cells.join(" | ")} |`;
    const headings = Array.from({ length: columns }, (_, i) => `Column ${i + 1}`);
    const table = [
      line(headings),
      line(headings.map(() => "---")),
      ...Array.from({ length: rows }, () => line(headings.map(() => " "))),
    ].join("\n");
    this.apply({ block: table, select: "Column 1" });
  }

  // The Code block dialog (T-0068): search the languages that highlight, then insert a fence for
  // one around the selection. A combobox: the field keeps focus, arrows move through the list,
  // Enter or a click inserts, Escape closes.
  openCodeDialog(opener) {
    if (!this.codeDialog) {
      this.codeDialog = this.buildCodeDialog();
    }
    const { dialog, search } = this.codeDialog;
    search.value = "";
    this.filterLanguages("");
    this.codeOpener = opener;
    dialog.showModal();
    search.focus();
  }

  buildCodeDialog() {
    let languages = [];
    try {
      languages = JSON.parse(this.getAttribute("code-languages") ?? "[]");
    } catch {
      languages = [];
    }
    this.languages = [{ name: "Plain text", fences: ["text"] }, ...languages];

    const id = `${this.textarea.id}-code`;
    const dialog = document.createElement("dialog");
    dialog.className = "code-dialog";
    dialog.setAttribute("aria-labelledby", `${id}-title`);
    const title = document.createElement("h2");
    title.id = `${id}-title`;
    title.textContent = "Insert a code block";
    const label = document.createElement("label");
    label.htmlFor = `${id}-search`;
    label.textContent = "Language";
    const search = document.createElement("input");
    search.id = `${id}-search`;
    search.type = "search";
    search.autocomplete = "off";
    search.spellcheck = false;
    search.setAttribute("role", "combobox");
    search.setAttribute("aria-autocomplete", "list");
    search.setAttribute("aria-expanded", "true");
    search.setAttribute("aria-controls", `${id}-list`);
    search.setAttribute("aria-describedby", `${id}-help`);
    const list = document.createElement("ul");
    list.id = `${id}-list`;
    list.className = "code-languages";
    list.setAttribute("role", "listbox");
    list.setAttribute("aria-label", "Languages");
    const help = document.createElement("p");
    help.id = `${id}-help`;
    help.className = "help";
    help.textContent = "Type a language or a fence name, such as python or cs. Enter inserts the highlighted one around the selected text.";
    const status = document.createElement("p");
    status.className = "visually-hidden";
    status.setAttribute("role", "status");
    const actions = document.createElement("div");
    actions.className = "dialog-actions";
    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.className = "btn";
    cancel.textContent = "Cancel";
    cancel.addEventListener("click", () => dialog.close());
    actions.append(cancel);
    dialog.append(title, label, search, list, help, status, actions);
    this.append(dialog);

    search.addEventListener("input", () => this.filterLanguages(search.value));
    search.addEventListener("keydown", (event) => {
      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        this.moveLanguage(event.key === "ArrowDown" ? 1 : -1);
      } else if (event.key === "Enter") {
        event.preventDefault();
        if (this.matches.length > 0) {
          this.insertCodeBlock(this.matches[this.active]);
        }
      } else if (event.key === "Escape") {
        // A search field's first Escape only clears it: close at once (F-0091 L4).
        event.preventDefault();
        dialog.close();
      }
    });
    list.addEventListener("click", (event) => {
      const option = event.target.closest("[role=option]");
      if (option) {
        this.insertCodeBlock(this.matches[Number(option.dataset.index)]);
      }
    });
    dialog.addEventListener("close", () => {
      if (!this.inserted) {
        this.codeOpener?.focus();
      }
      this.inserted = false;
    });
    return { dialog, search, list, status };
  }

  // Name or fence name containing the query; exact fence names first, then names that start with it.
  filterLanguages(query) {
    const q = query.trim().toLowerCase();
    const rank = (language) =>
      language.fences.includes(q) ? 0 : language.name.toLowerCase().startsWith(q) ? 1 : language.fences.some((f) => f.startsWith(q)) ? 2 : 3;
    this.matches = this.languages
      .filter((l) => q === "" || l.name.toLowerCase().includes(q) || l.fences.some((f) => f.includes(q)))
      .map((language, order) => ({ language, order }))
      .sort((a, b) => (q === "" ? a.order - b.order : rank(a.language) - rank(b.language) || a.order - b.order))
      .map((m) => m.language);
    this.active = 0;
    const { list, status } = this.codeDialog;
    list.replaceChildren(
      ...this.matches.map((language, index) => {
        const option = document.createElement("li");
        option.id = `${list.id}-${index}`;
        option.setAttribute("role", "option");
        option.dataset.index = String(index);
        const name = document.createElement("span");
        name.className = "name";
        name.textContent = language.name;
        const fences = document.createElement("span");
        fences.className = "fences";
        fences.textContent = language.fences.join(", ");
        option.append(name, fences);
        return option;
      }),
    );
    if (this.matches.length === 0) {
      // Said in the list too, not only to screen readers (F-0091 L4).
      const none = document.createElement("li");
      none.className = "none";
      none.textContent = "No language matches. Try its file extension, such as py or cs, or choose Plain text.";
      list.append(none);
    }
    status.textContent = this.matches.length === 0 ? "No language matches." : `${this.matches.length} languages.`;
    this.showActive();
  }

  moveLanguage(step) {
    if (this.matches.length === 0) {
      return;
    }
    this.active = (this.active + step + this.matches.length) % this.matches.length;
    this.showActive();
  }

  showActive() {
    const { search, list } = this.codeDialog;
    for (const option of list.children) {
      option.setAttribute("aria-selected", String(Number(option.dataset.index) === this.active));
    }
    const current = list.children[this.active];
    if (current) {
      search.setAttribute("aria-activedescendant", current.id);
      current.scrollIntoView({ block: "nearest" });
    } else {
      search.removeAttribute("aria-activedescendant");
    }
  }

  insertCodeBlock(language) {
    const fence = language.fences[0];
    this.inserted = true;
    this.codeDialog.dialog.close();
    const empty = this.textarea.selectionStart === this.textarea.selectionEnd;
    this.apply({ block: (selected) => `\`\`\`${fence}\n${selected || "code"}\n\`\`\`` });
    if (empty) {
      // The caret is at the block's end: select the placeholder, ready to type over. (Searching for
      // "code" could hit the fence name, as in typst-code.)
      const end = this.textarea.selectionEnd - "\n```".length;
      this.textarea.setSelectionRange(end - "code".length, end);
    }
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

    // Only the latest render is shown: an earlier, slower one arriving later is dropped (F-0119).
    let latest = 0;
    const render = debounce(async () => {
      const body = new FormData();
      body.set("markdown", this.textarea.value);
      body.set("__RequestVerificationToken", this.form.querySelector('input[name="__RequestVerificationToken"]')?.value ?? "");
      const request = ++latest;
      try {
        const response = await fetch(url, { method: "POST", body, credentials: "same-origin" });
        if (request !== latest) {
          return;
        }
        if (response.redirected) {
          // Sent to the sign-in page: the session ended (F-0114).
          this.preview.textContent = "Signed out: the preview stops until you sign in again in another tab. Your text is still here.";
        } else if (response.ok) {
          // Server-rendered and sanitized by the same renderer as the public site.
          this.preview.innerHTML = await response.text();
        }
      } catch {
        // Offline: the preview just stops updating.
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
      const element = this.form.querySelector(`ws-media-picker[for="${this.textarea.id}"]`);
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
    // Just saved: what was kept before the save is what was saved. What was kept since, at the
    // new version, stays: a reload of this address mustn't lose it (F-0110).
    const etag = this.form.querySelector('input[name="Form.ETag"]')?.value ?? "";
    if (this.hasAttribute("saved") && storage.get(this.key)?.etag !== etag) {
      storage.remove(this.key);
    }
    // The first save of a new item: its text was kept under "new post" or "new page" until now,
    // and would otherwise be offered on the next new item (F-0050).
    const created = this.getAttribute("created-key");
    if (created) {
      storage.remove(created);
    }

    // The title, summary and tags are kept with the text (F-0091 L20). Text kept from an earlier
    // version (saved since in another tab or by an app) is offered too, said as such (F-0110).
    const initial = this.snapshot();
    const stored = storage.get(this.key);
    if (stored && !this.same(stored, initial) && !this.empty(stored, initial)) {
      this.offerRestore(stored, stored.etag !== etag);
    }

    // Nothing to keep when it's what the page loaded with, or only cleared text (F-0091 L21).
    const save = debounce(() => {
      const now = this.snapshot();
      if (this.same(now, initial) || this.empty(now, initial)) {
        storage.remove(this.key);
      } else {
        storage.set(this.key, { ...now, etag, at: new Date().toISOString() });
      }
    }, 800);
    this.form.addEventListener("input", (event) => {
      if (event.target === this.textarea || KEPT_FIELDS.includes(event.target.name)) {
        save();
      }
    });
  }

  keptFields() {
    return KEPT_FIELDS.map((name) => this.form.querySelector(`[name="${name}"]`)).filter(Boolean);
  }

  snapshot() {
    return { text: this.textarea.value, fields: Object.fromEntries(this.keptFields().map((f) => [f.name, f.value])) };
  }

  // Entries from before the fields were kept have text only.
  same(a, b) {
    return a.text === b.text && KEPT_FIELDS.every((name) => (a.fields?.[name] ?? b.fields[name]) === b.fields[name]);
  }

  empty(entry, initial) {
    return entry.text.trim() === "" && KEPT_FIELDS.every((name) => (entry.fields?.[name] ?? initial.fields[name]) === initial.fields[name]);
  }

  offerRestore(stored, earlier) {
    const bar = document.createElement("div");
    bar.className = "notice restore";
    bar.setAttribute("role", "status");
    const message = document.createElement("span");
    message.textContent = earlier
      ? `Unsaved changes from ${stamp(stored.at)}, made to an earlier version: it has been saved since. Restore them only to compare and merge. `
      : `Unsaved changes from ${stamp(stored.at)}. `;
    const restore = document.createElement("button");
    restore.type = "button";
    restore.textContent = "Restore";
    restore.addEventListener("click", () => {
      for (const field of this.keptFields()) {
        if (typeof stored.fields?.[field.name] === "string") {
          field.value = stored.fields[field.name];
          field.dispatchEvent(new Event("input", { bubbles: true }));
          // The tags show as labels: draw them again from the field.
          field.closest("ws-tag-input")?.render?.();
        }
      }
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
      // The bar held the focus: give it to the text rather than the page (F-0119).
      this.textarea.focus();
    });
    bar.append(message, restore, " ", discard);
    this.before(bar);
  }
}

customElements.define("ws-md-editor", WsMdEditor);
