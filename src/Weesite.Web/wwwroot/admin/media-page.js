// The media page's enhancements (ADR 0006): files dropped on the upload area go to its file
// input, the chosen file's name shows, and each card's Copy button copies its Markdown. Without
// JavaScript, clicking the area opens the file chooser and the Markdown can be selected.

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
