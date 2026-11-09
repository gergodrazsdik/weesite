// <ws-theme-editor>: enhances the theme page (brief, Theme editor). Without JavaScript the page is
// hex fields, a Check button and a contrast table rendered by the server. This adds a colour
// picker beside each field, updates the contrast table as colours change (the same pairs and
// minimums as the server, from data-pairs), and shows a sample of both palettes. Colours are set
// through the CSSOM, which the strict CSP allows; no inline styles.
//
// Markup: <ws-theme-editor data-pairs="[{fg,bg,min}]"> around fieldsets[data-mode] holding
// inputs[data-token][data-default], and a table whose rows carry data-mode, data-fg, data-bg
// and data-min.

const HEX = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i;

const normalize = (value) => {
  const v = value.trim().toLowerCase();
  if (!HEX.test(v)) {
    return null;
  }
  return v.length === 4 ? `#${v[1]}${v[1]}${v[2]}${v[2]}${v[3]}${v[3]}` : v;
};

const luminance = (hex) => {
  const channel = (at) => {
    const c = parseInt(hex.slice(at, at + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5);
};

const ratio = (a, b) => {
  const [light, dark] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (light + 0.05) / (dark + 0.05);
};

class WsThemeEditor extends HTMLElement {
  connectedCallback() {
    if (this.preview) {
      return;
    }
    this.fields = [...this.querySelectorAll("input[data-token]")];
    for (const field of this.fields) {
      this.addPicker(field);
      field.addEventListener("input", () => this.update());
    }
    this.addPreview();
    this.update();
  }

  // The colour in effect for a mode and token: the field's, or the default.
  colour(mode, token) {
    const field = this.querySelector(`fieldset[data-mode="${mode}"] input[data-token="${token}"]`);
    return normalize(field?.value ?? "") ?? normalize(field?.dataset.default ?? "") ?? "#000000";
  }

  addPicker(field) {
    const picker = document.createElement("input");
    picker.type = "color";
    picker.className = "theme-picker";
    const label = field.labels?.[0]?.textContent?.trim() ?? field.dataset.token;
    const mode = field.closest("fieldset")?.querySelector("legend")?.textContent?.trim() ?? "";
    picker.setAttribute("aria-label", `${label} (${mode}): colour picker`);
    picker.value = normalize(field.value) ?? normalize(field.dataset.default) ?? "#000000";
    picker.addEventListener("input", () => {
      field.value = picker.value;
      this.update();
    });
    field.addEventListener("input", () => {
      const value = normalize(field.value);
      if (value) {
        picker.value = value;
      } else if (!field.value.trim()) {
        picker.value = normalize(field.dataset.default) ?? "#000000";
      }
    });
    field.after(picker);
  }

  addPreview() {
    this.preview = document.createElement("section");
    this.preview.className = "theme-preview";
    this.preview.setAttribute("aria-label", "Preview");
    for (const mode of ["light", "dark"]) {
      const sample = document.createElement("div");
      sample.className = "theme-sample";
      sample.dataset.mode = mode;
      const heading = document.createElement("p");
      heading.className = "sample-heading";
      heading.textContent = mode === "light" ? "Light" : "Dark";
      const text = document.createElement("p");
      text.textContent = "Body text with ";
      const link = document.createElement("a");
      link.href = "#";
      link.textContent = "a link";
      link.addEventListener("click", (event) => event.preventDefault());
      const code = document.createElement("code");
      code.textContent = "code";
      text.append(link, " and ", code, ".");
      const muted = document.createElement("p");
      muted.className = "sample-muted";
      muted.textContent = "2026-11-09 · 3 min read";
      const card = document.createElement("p");
      card.className = "sample-card";
      card.textContent = "A surface with a border and an accent.";
      sample.append(heading, text, muted, card);
      // Each mode's sample goes into its own card when there is one.
      const modeCard = this.querySelector(`fieldset[data-mode="${mode}"]`);
      if (modeCard) {
        modeCard.append(sample);
      } else {
        this.preview.append(sample);
      }
    }
    if (this.preview.childElementCount > 0) {
      this.querySelector(".theme-modes")?.after(this.preview);
    }
  }

  update() {
    for (const sample of this.querySelectorAll(".theme-sample")) {
      for (const field of this.fields) {
        const token = field.dataset.token;
        sample.style.setProperty(`--sample-${token}`, this.colour(sample.dataset.mode, token));
      }
    }

    for (const row of this.querySelectorAll("tr[data-fg]")) {
      const { mode, fg, bg, min } = row.dataset;
      const value = ratio(this.colour(mode, fg), this.colour(mode, bg));
      const cells = row.querySelectorAll("td");
      const minimum = Number(min);
      cells[2].textContent = `${value.toFixed(1)} : 1 (at least ${minimum % 1 === 0 ? minimum : minimum.toFixed(1)})`;
      const result = cells[3].querySelector(".pill") ?? cells[3];
      result.textContent = value >= minimum ? "Passes" : "Too low";
      result.classList.toggle("published", value >= minimum);
      result.classList.toggle("fails", value < minimum);
      row.classList.toggle("fails", value < minimum);
    }

    const rows = [...this.querySelectorAll("tr[data-fg]")];
    const failing = rows.filter((row) => row.classList.contains("fails")).length;
    const summary = this.querySelector("[data-summary]");
    if (summary) {
      summary.textContent = failing === 0 ? `all ${rows.length} checks pass` : `${failing} of ${rows.length} checks fail`;
      summary.classList.toggle("fails", failing > 0);
    }
  }
}

customElements.define("ws-theme-editor", WsThemeEditor);
