// <ws-media-picker>: inserts stored or newly uploaded media into the editor's Markdown textarea.
// Without JavaScript the element shows only its content: a link to the media page, where files are
// uploaded and their Markdown copied. With it, a button opens a dialog to upload a file or choose a
// stored one, and the Markdown goes in at the cursor.
//
// Markup: <ws-media-picker for="<textarea id>" list-url="/admin/media.json" upload-url="/admin/media"
// accept=".jpg,..."> inside a form that has an antiforgery token. The dialog is placed outside that
// form (forms can't nest).

const clean = (alt) => alt.trim().replaceAll("[", "").replaceAll("]", "");

// A preview of a chosen image in the upload area (T-0120), read as a data: URL, which the strict
// CSP allows for images (blob: URLs it doesn't). Other files show their name only.
const preview = (drop, file) => {
  let img = drop.querySelector("img.preview");
  if (!file?.type.startsWith("image/")) {
    img?.remove();
    return;
  }
  if (!img) {
    img = document.createElement("img");
    img.className = "preview";
    img.alt = "";
    drop.prepend(img);
  }
  const reader = new FileReader();
  reader.addEventListener("load", () => {
    img.src = String(reader.result);
  });
  reader.readAsDataURL(file);
};

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
    // The link to the media page is for pages without JavaScript.
    this.querySelector(".help")?.setAttribute("hidden", "");
  }

  // The editor may move this element into its toolbar: only a real removal drops the dialog.
  disconnectedCallback() {
    queueMicrotask(() => {
      if (!this.isConnected) {
        this.dialog?.remove();
      }
    });
  }

  // Opens the dialog with a file already chosen (dropped on the editor) and asks for its description.
  openWithFile(file) {
    this.open();
    const files = new DataTransfer();
    files.items.add(file);
    this.file.files = files.files;
    this.file.dispatchEvent(new Event("change"));
    this.alt.focus();
  }

  get token() {
    return this.form.querySelector('input[name="__RequestVerificationToken"]')?.value ?? "";
  }

  open() {
    if (!this.dialog?.isConnected) {
      this.dialog = this.build();
    }
    this.say("");
    this.select(null);
    this.dialog.showModal();
    this.load();
  }

  build() {
    const dialog = document.createElement("dialog");
    dialog.className = "media-picker";
    dialog.setAttribute("aria-labelledby", "media-picker-heading");
    dialog.innerHTML = `
      <form method="dialog">
        <header>
          <h2 id="media-picker-heading">Insert image or file</h2>
          <button type="button" class="close" aria-label="Close">×</button>
        </header>
        <div class="picker-body">
          <p class="notice" role="alert" hidden></p>
          <h3 class="side-label">Upload</h3>
          <div class="picker-upload">
            <label class="drop" for="media-picker-file">
              <span><strong>File:</strong> drop it here or choose one</span>
              <span class="chosen"></span>
              <input id="media-picker-file" type="file" name="file" class="visually-hidden">
            </label>
            <div class="field">
              <label for="media-picker-alt">Description of the image (alt text)</label>
              <input id="media-picker-alt" type="text" name="alt" maxlength="300" aria-describedby="media-picker-alt-help">
              <p id="media-picker-alt-help" class="help">Required for images: what it shows, for people who can't see it. Also used when you choose a stored image below.</p>
            </div>
          </div>
          <div class="list-head">
            <h3 class="side-label">Stored files</h3>
            <span class="count"></span>
          </div>
          <ul class="pick-grid" role="list"></ul>
        </div>
        <footer>
          <button type="button" class="btn cancel">Cancel</button>
          <button type="submit" class="btn primary">Insert</button>
        </footer>
      </form>`;
    this.status = dialog.querySelector('[role="alert"]');
    this.alt = dialog.querySelector("#media-picker-alt");
    this.list = dialog.querySelector(".pick-grid");
    this.count = dialog.querySelector(".count");
    this.file = dialog.querySelector("#media-picker-file");
    this.chosen = dialog.querySelector(".chosen");
    if (this.hasAttribute("accept")) {
      this.file.accept = this.getAttribute("accept");
    }
    // A chosen file and a selected stored file exclude each other.
    this.file.addEventListener("change", () => {
      this.chosen.textContent = this.file.files?.[0]?.name ?? "";
      preview(this.chosen.closest(".drop"), this.file.files?.[0]);
      if (this.file.files?.length) {
        this.select(null);
      }
    });
    const drop = dialog.querySelector(".drop");
    drop.addEventListener("dragover", (event) => {
      event.preventDefault();
      drop.classList.add("dropping");
    });
    drop.addEventListener("dragleave", () => drop.classList.remove("dropping"));
    drop.addEventListener("drop", (event) => {
      event.preventDefault();
      drop.classList.remove("dropping");
      if (event.dataTransfer?.files?.length) {
        this.file.files = event.dataTransfer.files;
        this.file.dispatchEvent(new Event("change"));
      }
    });
    dialog.querySelector("form").addEventListener("submit", (event) => {
      event.preventDefault();
      this.confirm();
    });
    for (const close of dialog.querySelectorAll(".close, .cancel")) {
      close.addEventListener("click", () => dialog.close());
    }
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
    this.count.textContent = `${items.length} file${items.length === 1 ? "" : "s"}`;
    this.list.replaceChildren(
      ...items.map((item) => {
        const li = document.createElement("li");
        const button = document.createElement("button");
        button.type = "button";
        button.setAttribute("aria-pressed", "false");
        const thumb = document.createElement("span");
        thumb.className = "thumb";
        if (item.thumbnail) {
          const img = document.createElement("img");
          img.src = item.thumbnail;
          img.alt = "";
          img.loading = "lazy";
          thumb.append(img);
        } else {
          thumb.textContent = item.name.split(".").pop()?.toUpperCase() ?? "";
        }
        const name = document.createElement("span");
        name.className = "name";
        name.textContent = item.name;
        button.append(thumb, name);
        button.addEventListener("click", () => {
          this.select(item, button);
          this.file.value = "";
          this.chosen.textContent = "";
          preview(this.chosen.closest(".drop"), null);
        });
        li.append(button);
        return li;
      }),
    );
  }

  select(item, button) {
    this.selected = item;
    for (const other of this.list.querySelectorAll("button")) {
      other.setAttribute("aria-pressed", String(other === button));
    }
  }

  // Insert: upload the chosen file, or use the selected stored one.
  confirm() {
    if (this.file.files?.length) {
      this.upload(this.file);
    } else if (this.selected) {
      this.choose(this.selected);
    } else {
      this.say("Choose a file to upload, or a stored file.");
    }
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
      this.chosen.textContent = "";
      preview(this.chosen.closest(".drop"), null);
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
