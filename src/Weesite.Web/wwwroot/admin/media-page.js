// The media page's enhancements (ADR 0006): files dropped on the upload area go to its file
// input, the chosen file's name shows with a preview of an image, and each card's Copy button
// copies its Markdown. Without JavaScript, clicking the area opens the file chooser and the
// Markdown can be selected.

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
