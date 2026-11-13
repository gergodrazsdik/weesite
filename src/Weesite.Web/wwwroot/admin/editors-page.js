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

if (navigator.clipboard?.writeText) {
  for (const copy of document.querySelectorAll("dialog [data-copy]")) {
    const field = document.getElementById(copy.dataset.copy);
    const status = copy.closest("dialog")?.querySelector(".copy-status");
    if (!field) {
      continue;
    }
    copy.hidden = false;
    copy.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(field.value);
        if (status) {
          status.textContent = "Copied.";
        }
      } catch {
        if (status) {
          status.textContent = "Couldn't copy: select the text and copy it instead.";
        }
      }
    });
  }
}
