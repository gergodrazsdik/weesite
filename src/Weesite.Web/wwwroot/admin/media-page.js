// The media page's enhancements (ADR 0006): files dropped on the upload area go to its file
// input, the chosen file's name shows with a preview of an image, and each card's Copy button
// copies its Markdown; the upload area opens from a button, and Archive and Delete ask in a
// dialog. Without JavaScript, the upload area is always there, clicking it opens the file
// chooser, the Markdown can be selected, and Archive and Delete open their pages.

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

for (const drop of document.querySelectorAll("label.drop")) {
  const input = drop.querySelector('input[type="file"]');
  const chosen = drop.querySelector(".chosen");
  if (!input) {
    continue;
  }
  const show = () => {
    if (chosen) {
      chosen.textContent = input.files?.[0]?.name ?? "";
    }
    preview(drop, input.files?.[0]);
  };
  input.addEventListener("change", show);
  drop.addEventListener("dragover", (event) => {
    event.preventDefault();
    drop.classList.add("dropping");
  });
  drop.addEventListener("dragleave", () => drop.classList.remove("dropping"));
  drop.addEventListener("drop", (event) => {
    event.preventDefault();
    drop.classList.remove("dropping");
    if (event.dataTransfer?.files?.length) {
      input.files = event.dataTransfer.files;
      show();
    }
  });
}

if (navigator.clipboard) {
  for (const button of document.querySelectorAll("button[data-copy]")) {
    const code = document.getElementById(button.dataset.copy);
    if (!code) {
      continue;
    }
    button.hidden = false;
    button.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(code.textContent ?? "");
        button.textContent = "Copied";
      } catch {
        button.textContent = "Select it";
      }
      setTimeout(() => {
        button.textContent = "Copy";
      }, 2000);
    });
  }
}

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
// and shown in place, so its warnings and checks are the same. Cancel closes the dialog and leaves
// the list as it was; after the action the list comes back with its filter and tab. Anything that
// goes wrong falls back to opening the page.
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
    const title = document.createElement("h2");
    title.id = "confirm-dialog-title";
    title.textContent = heading.textContent;
    dialog.setAttribute("aria-labelledby", title.id);
    dialog.append(title, document.importNode(section, true));
    dialog.querySelector("[data-cancel]")?.addEventListener("click", (cancel) => {
      cancel.preventDefault();
      dialog.close();
    });
    dialog.addEventListener("close", () => {
      dialog.remove();
      link.focus();
    });
    document.body.append(dialog);
    dialog.showModal();
    (dialog.querySelector('input:not([type="hidden"])') ?? dialog.querySelector('button[type="submit"]'))?.focus();
  });
}
