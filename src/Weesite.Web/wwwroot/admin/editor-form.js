// The edit page's smaller enhancements (ADR 0006): the summary's character count, the social card
// preview following the image field, <ws-tag-input>, which shows the comma-separated tags as
// removable labels with suggestions from the tags already in use, and a question before leaving
// with unsaved changes. Without JavaScript the plain fields work as before.

// "45 / 160": search engines show about 160 characters of a summary.
for (const count of document.querySelectorAll("[data-count-for]")) {
  const field = document.getElementById(count.dataset.countFor);
  if (!field) {
    continue;
  }
  const max = Number(count.textContent.split("/")[1]) || 160;
  const update = () => {
    count.textContent = `${field.value.length} / ${max}`;
    count.classList.toggle("over", field.value.length > max);
  };
  field.addEventListener("input", update);
  update();
}

// The card shows the chosen image, or the generated card (public items only), or a note. Only a
// stored media file is previewed: the name must look like the ones /media/ serves (MediaEndpoint),
// and the address is rebuilt from the encoded name rather than taken from the field as typed.
const MEDIA_PATH = /^\/media\/([0-9a-f]{8,64}-[a-z0-9][a-z0-9._-]{0,200})$/u;
const mediaSource = (value) => {
  const match = MEDIA_PATH.exec(value.trim());
  return match ? `/media/${encodeURIComponent(match[1])}` : null;
};

const card = document.querySelector("img.og-card");
const note = document.querySelector(".og-note");
const image = document.getElementById("image");
if (card && note && image) {
  image.addEventListener("change", () => {
    const src = mediaSource(image.value) || card.dataset.generated;
    if (src) {
      card.src = src;
    }
    card.hidden = !src;
    note.hidden = Boolean(src);
  });
}

class WsTagInput extends HTMLElement {
  connectedCallback() {
    this.field = this.querySelector("input");
    if (!this.field || this.box) {
      return;
    }
    this.colours = new Map(
      [...this.querySelectorAll("datalist option")].map((option) => [option.value, option.dataset.colour ?? ""]),
    );

    // The original field stays in the form, hidden, and holds the comma-separated value.
    this.box = document.createElement("div");
    this.box.className = "chips";
    this.entry = document.createElement("input");
    this.entry.type = "text";
    this.entry.id = `${this.field.id}-entry`;
    this.entry.placeholder = "add tag…";
    this.entry.autocomplete = "off";
    this.entry.setAttribute("list", this.field.getAttribute("list") ?? "");
    this.entry.setAttribute("aria-describedby", this.field.getAttribute("aria-describedby") ?? "");
    const label = this.querySelector("label");
    if (label) {
      label.htmlFor = this.entry.id;
      label.textContent = "Add a tag";
    }
    this.field.hidden = true;
    this.field.removeAttribute("list");
    this.field.after(this.box);
    this.box.append(this.entry);
    // Says which tag went in or out, for screen readers (F-0091 L29).
    this.announcer = document.createElement("span");
    this.announcer.className = "visually-hidden";
    this.announcer.setAttribute("role", "status");
    this.box.after(this.announcer);

    this.entry.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === ",") {
        event.preventDefault();
        this.add(this.entry.value);
      } else if (event.key === "Backspace" && this.entry.value === "") {
        const tags = this.tags;
        const removed = tags.pop();
        this.tags = tags;
        if (removed) {
          this.announcer.textContent = `Removed #${removed}.`;
        }
      }
    });
    this.entry.addEventListener("blur", () => this.add(this.entry.value));
    this.render();
  }

  get tags() {
    return this.field.value.split(",").map((t) => t.trim()).filter(Boolean);
  }

  set tags(tags) {
    this.field.value = [...new Set(tags)].join(", ");
    this.render();
    // The hidden field changed: the editor's autosave and leave check listen for input.
    this.field.dispatchEvent(new Event("input", { bubbles: true }));
  }

  add(text) {
    // As the server takes tags: lowercase, composed, spaces as hyphens (F-0091 L1).
    const tag = text.trim().replace(/^#/u, "").normalize("NFC").toLowerCase().replace(/\s+/gu, "-");
    this.entry.value = "";
    if (tag && !this.tags.includes(tag)) {
      this.tags = [...this.tags, tag];
      this.announcer.textContent = `Added #${tag}.`;
    }
  }

  // Only the chips are replaced: the entry stays where it is, so it keeps the focus and the next
  // tag can be typed straight away (F-0090).
  render() {
    for (const chip of this.box.querySelectorAll(".tag")) {
      chip.remove();
    }
    this.entry.before(
      ...this.tags.map((tag) => {
        const chip = document.createElement("span");
        chip.className = `tag ${this.colours.get(tag) ?? ""}`.trim();
        chip.textContent = `#${tag}`;
        const remove = document.createElement("button");
        remove.type = "button";
        remove.textContent = "×";
        remove.setAttribute("aria-label", `Remove ${tag}`);
        remove.addEventListener("click", () => {
          this.tags = this.tags.filter((t) => t !== tag);
          this.announcer.textContent = `Removed #${tag}.`;
          this.entry.focus();
        });
        chip.append(remove);
        return chip;
      }),
    );
  }
}

customElements.define("ws-tag-input", WsTagInput);

// A new item's slug follows its title until the slug is edited (F-0086): lowercase letters of any
// script and digits ("Első bejegyzés" gives "első-bejegyzés", F-0091 L1), single hyphens between
// them, at most 100 characters, cut at a hyphen. An item that has a slug keeps it.
const title = document.getElementById("title");
const slug = document.getElementById("slug");
if (title && slug && slug.value === "") {
  let edited = false;
  slug.addEventListener("input", () => {
    edited = slug.value !== "";
  });
  title.addEventListener("input", () => {
    if (edited) {
      return;
    }
    let value = title.value
      .normalize("NFC")
      .toLowerCase()
      .replace(/[^\p{L}\p{M}\p{N}]+/gu, "-")
      .replace(/^-|-$/gu, "");
    if (value.length > 100) {
      value = value.slice(0, 100);
      value = value.includes("-") ? value.slice(0, value.lastIndexOf("-")) : value;
    }
    slug.value = value;
  });
}

// Ctrl+S (or Cmd+S) saves from anywhere in the form, not only in the text (F-0086); the
// Markdown editor handles its own and marks the event handled.
const form = document.querySelector("form.editor");
form?.addEventListener("keydown", (event) => {
  if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === "s" && !event.defaultPrevented) {
    event.preventDefault();
    form.querySelector('button[type="submit"][value="save"]')?.click();
  }
});

// Leaving with unsaved changes asks first (T-0109): the browser's own "Leave site?" question, the
// only kind a page can raise when it's closed or left. Changes are the form's fields against what
// they held once the page had loaded; a page the server rendered with unsaved text (a preview, a
// save that failed, an earlier version to restore) counts as changed from the start. Saving and
// previewing submit the form, which doesn't ask.
const editor = document.querySelector("form.editor");
if (editor) {
  const fields = () => {
    const data = new FormData(editor);
    data.delete("__RequestVerificationToken");
    return new URLSearchParams(data).toString();
  };
  let saved = null;
  let submitting = false;
  window.addEventListener("load", () => {
    saved = fields();
  });
  editor.addEventListener("submit", () => {
    submitting = true;
  });
  // Back from the browser's cache (after a submit or a cancelled leave): ask again from here on.
  window.addEventListener("pageshow", () => {
    submitting = false;
  });
  window.addEventListener("beforeunload", (event) => {
    if (submitting || saved === null) {
      return;
    }
    if (editor.dataset.unsaved === "true" || fields() !== saved) {
      event.preventDefault();
      event.returnValue = "";
    }
  });
}
