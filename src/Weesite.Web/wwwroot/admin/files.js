// Upload areas in the admin (F-0120): a preview of a chosen image, and files dropped on the area
// going to its file input. Shared by the media page and the media picker.

// A preview of a chosen image in the upload area (T-0120), read as a data: URL, which the strict
// CSP allows for images (blob: URLs it doesn't). Other files show their name only.
export const preview = (drop, file) => {
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

// Files dropped on the area go to the input, which then fires "change" as if they were chosen.
export const acceptDrops = (drop, input) => {
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
      input.dispatchEvent(new Event("change"));
    }
  });
};
