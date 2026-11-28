// "Select all on this page" for the Comments page's bulk form (T-0184). Without this script the
// checkbox stays hidden and each comment is ticked on its own; with it, the checkbox ticks or
// clears every comment on the page, and shows mixed when only some are ticked.
const form = document.getElementById("bulk-moderation");
const all = form?.querySelector("[data-select-all]");
const box = all?.querySelector('input[type="checkbox"]');
if (form instanceof HTMLFormElement && all instanceof HTMLElement && box instanceof HTMLInputElement) {
  const items = () => [...document.querySelectorAll('input[type="checkbox"][form="bulk-moderation"]')];
  const sync = () => {
    const ticked = items().filter((item) => item.checked).length;
    box.checked = ticked > 0 && ticked === items().length;
    box.indeterminate = ticked > 0 && ticked < items().length;
  };

  all.hidden = false;
  box.addEventListener("change", () => {
    for (const item of items()) {
      item.checked = box.checked;
    }
    sync();
  });
  for (const item of items()) {
    item.addEventListener("change", sync);
  }
  sync();
}
