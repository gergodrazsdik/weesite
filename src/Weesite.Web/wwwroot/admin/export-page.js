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
