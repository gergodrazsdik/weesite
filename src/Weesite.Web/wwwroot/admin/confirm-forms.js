// A form with data-confirm asks before it posts (F-0091 L13): deleting a message or a comment,
// removing an editor, revoking a connected app, signing out everywhere. The admin's one way of
// asking (T-0177, U-02), as on the media list: a dialog with a heading (data-confirm-title, the
// question), the form's text (data-confirm) under it, then Cancel and the action's button
// (data-confirm-button), in that order. Cancel or Escape leaves everything as it was. Only what
// can't be undone and is big (deleting a post, a page or a media file, the reset) also asks for
// something typed: those have pages of their own. Confirming adds the field "confirmed": a post
// without it, such as without JavaScript, changes nothing (U-67, Admin/Confirmation.cs).
let count = 0;
for (const form of document.querySelectorAll("form[data-confirm]")) {
  let confirmed = false;
  form.addEventListener("submit", (event) => {
    if (confirmed) {
      return;
    }
    event.preventDefault();
    const opener = event.submitter ?? form.querySelector('button[type="submit"]');
    const id = `confirm-form-${++count}`;
    const dialog = document.createElement("dialog");
    dialog.className = "code-dialog confirm-dialog";
    dialog.setAttribute("role", "alertdialog");
    const title = document.createElement("h2");
    title.id = `${id}-title`;
    title.textContent = form.dataset.confirmTitle ?? form.dataset.confirm ?? "";
    dialog.setAttribute("aria-labelledby", title.id);
    dialog.append(title);
    if (form.dataset.confirmTitle && form.dataset.confirm) {
      const text = document.createElement("p");
      text.id = `${id}-text`;
      text.textContent = form.dataset.confirm;
      dialog.setAttribute("aria-describedby", text.id);
      dialog.append(text);
    }
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
    dialog.append(actions);
    cancel.addEventListener("click", () => dialog.close());
    go.addEventListener("click", () => {
      confirmed = true;
      if (!form.querySelector('input[name="confirmed"]')) {
        const field = document.createElement("input");
        field.type = "hidden";
        field.name = "confirmed";
        field.value = "1";
        form.append(field);
      }
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
