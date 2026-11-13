// A form with data-confirm asks before it posts (F-0091 L13): removing an editor, revoking a
// connected app. The question is a dialog with the form's text, Cancel, and the action's button
// (data-confirm-button); Cancel or Escape leaves everything as it was. Without JavaScript the
// form posts at once, as before.
for (const form of document.querySelectorAll("form[data-confirm]")) {
  let confirmed = false;
  form.addEventListener("submit", (event) => {
    if (confirmed) {
      return;
    }
    event.preventDefault();
    const opener = event.submitter ?? form.querySelector('button[type="submit"]');
    const dialog = document.createElement("dialog");
    dialog.className = "code-dialog confirm-dialog";
    dialog.setAttribute("role", "alertdialog");
    const text = document.createElement("p");
    text.id = "confirm-form-text";
    text.textContent = form.dataset.confirm ?? "";
    dialog.setAttribute("aria-labelledby", text.id);
    const actions = document.createElement("div");
    actions.className = "dialog-actions";
    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.className = "btn";
    cancel.textContent = "Cancel";
    const go = document.createElement("button");
    go.type = "button";
    go.className = "btn danger";
    go.textContent = form.dataset.confirmButton ?? "OK";
    actions.append(cancel, go);
    dialog.append(text, actions);
    cancel.addEventListener("click", () => dialog.close());
    go.addEventListener("click", () => {
      confirmed = true;
      dialog.close();
      form.requestSubmit(opener ?? undefined);
    });
    dialog.addEventListener("close", () => {
      dialog.remove();
      if (!confirmed) {
        opener?.focus();
      }
    });
    document.body.append(dialog);
    dialog.showModal();
    cancel.focus();
  });
}
