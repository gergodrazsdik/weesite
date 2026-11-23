// A page's explanation under the (i) beside its title (T-0160) is a <details>, which opens and
// closes with a click, Enter or Space by itself. This adds what a popup is expected to do: Escape
// closes it and gives the focus back to the (i), and a click elsewhere closes it.

const open = () => [...document.querySelectorAll("details.page-intro[open]")];

// Escape only while the focus is in the explanation (F-0224): elsewhere it belongs to what has the
// focus, such as a dialog, a menu or the Markdown editor, which may have handled it already.
document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape" || event.defaultPrevented) {
    return;
  }
  const details = event.target instanceof Element ? event.target.closest("details.page-intro[open]") : null;
  if (!details) {
    return;
  }
  details.open = false;
  details.querySelector("summary")?.focus();
  event.preventDefault();
});

document.addEventListener("pointerdown", (event) => {
  for (const details of open()) {
    if (!details.contains(event.target)) {
      details.open = false;
    }
  }
});
