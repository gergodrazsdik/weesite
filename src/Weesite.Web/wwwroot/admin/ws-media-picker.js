// <ws-media-picker>: inserts stored or newly uploaded media into the editor's Markdown textarea.
// Without JavaScript the element shows only its content: a link to the media page, where files are
// uploaded and their Markdown copied. With it, a button opens a dialog to upload a file or choose a
// stored one, and the Markdown goes in at the cursor.
//
// Markup: <ws-media-picker for="<textarea id>" list-url="/admin/media.json" upload-url="/admin/media"
// accept=".jpg,..."> inside a form that has an antiforgery token. The dialog is placed outside that
// form (forms can't nest).

const clean = (alt) => alt.trim().replaceAll("[", "").replaceAll("]", "");

class WsMediaPicker extends HTMLElement {
  connectedCallback() {
    this.textarea = document.getElementById(this.getAttribute("for") ?? "");
    this.form = this.textarea?.form;
    if (!this.textarea || !this.form || this.button) {
      return;
    }

    this.button = document.createElement("button");
    this.button.type = "button";
    this.button.textContent = "Insert image or file";
    this.button.setAttribute("aria-haspopup", "dialog");
    this.button.addEventListener("click", () => this.open());
    this.prepend(this.button);
  }

  disconnectedCallback() {
    this.dialog?.remove();
  }

  get token() {
    return this.form.querySelector('input[name="__RequestVerificationToken"]')?.value ?? "";
  }

  open() {
    this.dialog ??= this.build();
    this.say("");
    this.dialog.showModal();
    this.load();
  }

  build() {
    const dialog = document.createElement("dialog");
    dialog.className = "media-picker";
    dialog.setAttribute("aria-labelledby", "media-picker-heading");
    dialog.innerHTML = `
      <header>
        <h2 id="media-picker-heading">Insert image or file</h2>
        <button type="button" class="close">Close</button>
      </header>
      <p class="notice" role="alert" hidden></p>
      <form class="stacked-form" method="dialog">
        <h3>Upload</h3>
        <label for="media-picker-file">File</label>
        <input id="media-picker-file" type="file" name="file" required>
        <label for="media-picker-alt">Description of the image (alt text)</label>
        <input id="media-picker-alt" type="text" name="alt" maxlength="300" aria-describedby="media-picker-alt-help">
        <p id="media-picker-alt-help" class="help">Required for images: what it shows, for people who can't see it. Also used when you choose a stored image below.</p>
        <button type="submit" value="upload">Upload and insert</button>
      </form>
      <h3>Stored files</h3>
      <ul class="media-choices" role="list"></ul>`;
    this.status = dialog.querySelector('[role="alert"]');
    this.alt = dialog.querySelector("#media-picker-alt");
    this.list = dialog.querySelector(".media-choices");
    const file = dialog.querySelector("#media-picker-file");
    if (this.hasAttribute("accept")) {
      file.accept = this.getAttribute("accept");
    }
    dialog.querySelector("form").addEventListener("submit", (event) => {
      event.preventDefault();
      this.upload(file);
    });
    dialog.querySelector(".close").addEventListener("click", () => dialog.close());
    dialog.addEventListener("close", () => this.button.focus());
    document.body.append(dialog);
    return dialog;
  }

  say(message) {
    this.status.textContent = message;
    this.status.hidden = !message;
  }

  async load() {
    try {
      const response = await fetch(this.getAttribute("list-url"), { credentials: "same-origin", headers: { Accept: "application/json" } });
      if (!response.ok) {
        throw new Error(String(response.status));
      }
      this.show(await response.json());
    } catch {
      this.say("The stored files couldn't be loaded. The media page lists them too.");
    }
  }

  show(items) {
    this.list.replaceChildren(
      ...items.map((item) => {
        const li = document.createElement("li");
        const button = document.createElement("button");
        button.type = "button";
        if (item.thumbnail) {
          const img = document.createElement("img");
          img.src = item.thumbnail;
          img.alt = "";
          img.width = 96;
          img.height = 64;
          img.loading = "lazy";
          button.append(img);
        }
        const name = document.createElement("span");
        name.textContent = item.name;
        button.append(name);
        button.addEventListener("click", () => this.choose(item));
        li.append(button);
        return li;
      }),
    );
  }

  choose(item) {
    if (!item.isImage) {
      this.insert(item.markdown);
      return;
    }
    const alt = clean(this.alt.value);
    if (!alt) {
      this.say("Describe the image first (alt text).");
      this.alt.focus();
      return;
    }
    this.insert(`![${alt}](${item.path})`);
  }

  async upload(file) {
    if (!file.files?.length) {
      this.say("Choose a file to upload.");
      file.focus();
      return;
    }
    const body = new FormData();
    body.set("file", file.files[0]);
    body.set("alt", this.alt.value);
    body.set("__RequestVerificationToken", this.token);
    this.say("");
    try {
      const response = await fetch(this.getAttribute("upload-url"), {
        method: "POST",
        body,
        credentials: "same-origin",
        headers: { Accept: "application/json" },
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) {
        this.say(result.error ?? "The upload failed.");
        return;
      }
      file.value = "";
      this.insert(result.markdown);
    } catch {
      this.say("The upload failed. Check the connection and try again.");
    }
  }

  // Puts the Markdown at the cursor, on its own line, and tells the editor the text changed.
  insert(markdown) {
    const { textarea } = this;
    const { selectionStart: start, selectionEnd: end, value } = textarea;
    const before = start > 0 && value[start - 1] !== "\n" ? "\n" : "";
    const text = before + markdown + "\n";
    textarea.setRangeText(text, start, end, "end");
    textarea.dispatchEvent(new Event("input", { bubbles: true }));
    this.alt.value = "";
    this.dialog.close();
    textarea.focus();
  }
}

customElements.define("ws-media-picker", WsMediaPicker);
