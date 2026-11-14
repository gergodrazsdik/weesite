import { setUpCopy } from "./clipboard.js";

// The editors page (ADR 0010): each editor's Invitation button opens their invitation in a
// dialog, with Copy where the clipboard works. Close is the dialog's own form; Escape closes it
// too, and focus goes back to the button.
for (const button of document.querySelectorAll("[data-show-dialog]")) {
  const dialog = document.getElementById(button.dataset.showDialog);
  if (!(dialog instanceof HTMLDialogElement)) {
    continue;
  }
  button.hidden = false;
  button.addEventListener("click", () => {
    const status = dialog.querySelector(".copy-status");
    if (status) {
      status.textContent = "";
    }
    dialog.showModal();
  });
  dialog.addEventListener("close", () => button.focus());
}

setUpCopy(document.querySelectorAll("dialog [data-copy]"), (button) => button.closest("dialog")?.querySelector(".copy-status"));
