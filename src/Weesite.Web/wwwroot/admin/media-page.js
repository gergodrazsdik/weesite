import { setUpCopy } from "./clipboard.js";
import { acceptDrops, preview } from "./files.js";

// The media page's enhancements (ADR 0006): files dropped on the upload area go to its file
// input, the chosen file's name shows with a preview of an image, and each card's Copy button
// copies its Markdown; the upload area opens from a button, and Archive and Delete ask in a
// dialog. Without JavaScript, the upload area is always there, clicking it opens the file
// chooser, the Markdown can be selected, and Archive and Delete open their pages.

for (const drop of document.querySelectorAll("label.drop")) {
  const input = drop.querySelector('input[type="file"]');
  const chosen = drop.querySelector(".chosen");
  if (!input) {
    continue;
  }
  input.addEventListener("change", () => {
    if (chosen) {
      chosen.textContent = input.files?.[0]?.name ?? "";
    }
    preview(drop, input.files?.[0]);
  });
  acceptDrops(drop, input);
}

// After an archive, a delete, an upload or a new description the list loads again with a notice
// that says what happened (data-focus): notice-focus.js gives it the focus (U-31), as it does after
// every admin save.

// Each card's Copy button; the outcome is said in the page's status region (F-0117).
setUpCopy(document.querySelectorAll("button[data-copy]"), () => document.getElementById("copy-status"));

// The upload area opens from "Upload a file" and closes with Cancel (F-0094); it stays open after
// a refused upload, so the reason shows beside it. Without JavaScript it's always there.
const upload = document.getElementById("upload");
const openUpload = document.querySelector("[data-upload-open]");
const cancelUpload = upload?.querySelector("[data-upload-cancel]");
const fileInput = upload?.querySelector('input[type="file"]');
if (upload && openUpload && cancelUpload && fileInput) {
  const setOpen = (open) => {
    upload.hidden = !open;
    openUpload.setAttribute("aria-expanded", String(open));
  };
  openUpload.hidden = false;
  cancelUpload.hidden = false;
  setOpen(upload.dataset.open === "true");
  openUpload.addEventListener("click", () => {
    setOpen(true);
    fileInput.focus();
  });
  cancelUpload.addEventListener("click", () => {
    upload.reset();
    fileInput.dispatchEvent(new Event("change"));
    setOpen(false);
    openUpload.focus();
  });

  // A chosen file or a description not uploaded yet asks before the page is left or reloaded
  // (F-0095), as the editor does; uploading doesn't.
  let submitting = false;
  upload.addEventListener("submit", () => {
    submitting = true;
  });
  window.addEventListener("pageshow", () => {
    submitting = false;
  });
  window.addEventListener("beforeunload", (event) => {
    const alt = upload.querySelector('input[name="alt"]');
    if (!submitting && (fileInput.files?.length || alt?.value.trim())) {
      event.preventDefault();
      event.returnValue = "";
    }
  });
}

// Archive and Delete ask on the list, in a dialog (F-0096): the confirmation page's form, fetched
// and shown in place, so its warnings and checks are the same. It's the admin's one way of asking
// (U-02, as confirm-forms.js builds it): the page's heading, what happens, Cancel then the action,
// and for a delete the name typed. Cancel closes the dialog and leaves the list as it was; after
// the action the list comes back with its filter and tab. Anything that goes wrong falls back to
// opening the page.
for (const link of document.querySelectorAll("a[data-confirm]")) {
  link.addEventListener("click", async (event) => {
    event.preventDefault();
    let page;
    try {
      const response = await fetch(link.href, { credentials: "same-origin" });
      if (!response.ok) {
        throw new Error(String(response.status));
      }
      page = new DOMParser().parseFromString(await response.text(), "text/html");
    } catch {
      window.location.href = link.href;
      return;
    }
    const heading = page.querySelector(".admin-body h1");
    const section = page.querySelector(".admin-body section");
    if (!heading || !section) {
      window.location.href = link.href;
      return;
    }
    const dialog = document.createElement("dialog");
    dialog.className = "code-dialog confirm-dialog";
    // An alert dialog, labelled by the question and described by what happens, as confirm-forms.js
    // builds it.
    dialog.setAttribute("role", "alertdialog");
    const title = document.createElement("h2");
    title.id = "confirm-dialog-title";
    title.textContent = heading.textContent;
    dialog.setAttribute("aria-labelledby", title.id);
    // The page names its section with a hidden copy of its title: in the dialog the title is there
    // to see, once.
    const body = document.importNode(section, true);
    body.querySelector("h2.visually-hidden")?.remove();
    body.removeAttribute("aria-labelledby");
    // The description: the warning about where it's used, if any, and what the action does; the
    // form's own help stays with its field.
    const described = [...body.querySelectorAll(":scope > p, :scope > .notice")];
    described.forEach((part, index) => {
      part.id ||= `confirm-dialog-text-${index + 1}`;
    });
    if (described.length > 0) {
      dialog.setAttribute("aria-describedby", described.map((part) => part.id).join(" "));
    }
    dialog.append(title, body);
    dialog.querySelector("[data-cancel]")?.addEventListener("click", (cancel) => {
      cancel.preventDefault();
      dialog.close();
    });
    dialog.addEventListener("close", () => {
      dialog.remove();
      link.focus();
    });
    document.body.append(dialog);
    // The delete page's Copy for the name to type: its own script doesn't run in the dialog.
    setUpCopy(dialog.querySelectorAll(".typed-confirm [data-copy]"), (button) =>
      button.closest(".typed-confirm")?.querySelector(".copy-status"),
    );
    dialog.showModal();
    (dialog.querySelector('input:not([type="hidden"])') ?? dialog.querySelector('button[type="submit"]'))?.focus();
  });
}
