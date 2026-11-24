// The export page (T-0050): Copy buttons for the title, cover address and HTML source, and
// "Copy as rich text", which puts the HTML on the clipboard as formatted text (with plain text
// beside it) for pasting into a web editor. Buttons appear only where the clipboard works;
// without them the fields are there to select and copy by hand.

import { setUpCopy } from "./clipboard.js";

const status = document.querySelector(".copy-status");
const say = (text) => {
  if (status) {
    status.textContent = text;
  }
};

setUpCopy(document.querySelectorAll("[data-copy]"), () => status);

const rich = document.querySelector("[data-copy-rich]");
const html = document.getElementById("export-html");
const text = document.getElementById("export-text");
if (rich && html && text && navigator.clipboard?.write && window.ClipboardItem) {
  rich.hidden = false;
  rich.addEventListener("click", async () => {
    try {
      await navigator.clipboard.write([
        new ClipboardItem({
          "text/html": new Blob([html.value], { type: "text/html" }),
          "text/plain": new Blob([text.value], { type: "text/plain" }),
        }),
      ]);
      say("Copied as rich text: paste it into the other editor.");
    } catch {
      say("Couldn't copy: download the file or copy the HTML source instead.");
    }
  });
}

// The footer box applies as it's ticked (U-46): the export is fetched again with it and its parts
// (the changes, the downloads, the HTML and text, the preview) put in place, so the box keeps the
// focus and Update isn't needed. If that fails, the form is sent as without scripts.
const options = document.querySelector("form.export-options");
const footer = options?.querySelector('input[name="footer"]');
const update = options?.querySelector("[data-export-update]");
if (options && footer && update) {
  update.hidden = true;
  let latest = 0;
  footer.addEventListener("change", async () => {
    const url = new URL(options.action, location.href);
    url.search = new URLSearchParams(new FormData(options)).toString();
    const request = ++latest;
    try {
      const response = await fetch(url, { credentials: "same-origin", headers: { Accept: "text/html" } });
      if (!response.ok || response.redirected) {
        throw new Error(String(response.status));
      }
      const page = new DOMParser().parseFromString(await response.text(), "text/html");
      if (request !== latest) {
        return;
      }
      for (const part of document.querySelectorAll("[data-export-part]")) {
        const fresh = page.querySelector(`[data-export-part="${part.dataset.exportPart}"]`);
        if (fresh) {
          part.replaceWith(document.importNode(fresh, true));
        }
      }
      for (const id of ["export-html", "export-text"]) {
        const field = document.getElementById(id);
        const fresh = page.getElementById(id);
        if (field instanceof HTMLTextAreaElement && fresh instanceof HTMLTextAreaElement) {
          field.value = fresh.value;
        }
      }
      history.replaceState(null, "", url);
      say(footer.checked ? "The export now ends with the footer." : "The export now ends without the footer.");
    } catch {
      options.requestSubmit();
    }
  });
}
