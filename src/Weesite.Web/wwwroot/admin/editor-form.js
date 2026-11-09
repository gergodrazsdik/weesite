// The edit page's smaller enhancements (ADR 0006): the summary's character count, the social card
// preview following the image field, and <ws-tag-input>, which shows the comma-separated tags as
// removable labels with suggestions from the tags already in use. Without JavaScript the plain
// fields work as before.

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

// The card shows the chosen image, or the generated card when there's none. Only a stored media
// file is previewed: the name must look like the ones /media/ serves (MediaEndpoint), and the
// address is rebuilt from the encoded name rather than taken from the field as typed.
const MEDIA_PATH = /^\/media\/([0-9a-f]{8,64}-[a-z0-9][a-z0-9._-]{0,200})$/u;
const mediaSource = (value) => {
  const match = MEDIA_PATH.exec(value.trim());
  return match ? `/media/${encodeURIComponent(match[1])}` : null;
};

const card = document.querySelector("img.og-card");
const image = document.getElementById("image");
if (card && image) {
  image.addEventListener("change", () => {
    card.src = mediaSource(image.value) ?? card.dataset.generated;
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

    this.entry.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === ",") {
        event.preventDefault();
        this.add(this.entry.value);
      } else if (event.key === "Backspace" && this.entry.value === "") {
        const tags = this.tags;
        tags.pop();
        this.tags = tags;
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
  }

  add(text) {
    const tag = text.trim().replace(/^#/u, "").toLowerCase();
    this.entry.value = "";
    if (tag) {
      this.tags = [...this.tags, tag];
    }
  }

  render() {
    this.box.replaceChildren(
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
          this.entry.focus();
        });
        chip.append(remove);
        return chip;
      }),
      this.entry,
    );
  }
}

customElements.define("ws-tag-input", WsTagInput);
