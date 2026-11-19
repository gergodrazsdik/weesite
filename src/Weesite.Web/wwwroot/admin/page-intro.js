// A page's explanation under the (i) beside its title (T-0160) is a <details>, which opens and
// closes with a click, Enter or Space by itself. This adds what a popup is expected to do: Escape
// closes it and gives the focus back to the (i), and a click elsewhere closes it.

const open = () => [...document.querySelectorAll("details.page-intro[open]")];

document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") {
    return;
  }
  for (const details of open()) {
    details.open = false;
    details.querySelector("summary")?.focus();
  }
});

document.addEventListener("pointerdown", (event) => {
  for (const details of open()) {
    if (!details.contains(event.target)) {
      details.open = false;
    }
  }
});
