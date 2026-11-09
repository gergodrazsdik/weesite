// The export page (T-0050): Copy buttons for the title, cover address and HTML source, and
// "Copy as rich text", which puts the HTML on the clipboard as formatted text (with plain text
// beside it) for pasting into a web editor. Buttons appear only where the clipboard works;
// without them the fields are there to select and copy by hand.

const status = document.querySelector(".copy-status");
const say = (text) => {
  if (status) {
    status.textContent = text;
  }
};

if (navigator.clipboard?.writeText) {
  for (const button of document.querySelectorAll("[data-copy]")) {
    const field = document.getElementById(button.dataset.copy);
    if (!field) {
      continue;
    }
    button.hidden = false;
    button.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(field.value ?? field.textContent ?? "");
        say("Copied.");
      } catch {
        say("Couldn't copy: select the text and copy it instead.");
      }
    });
  }
}

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
