// Suggested tag colours (T-0144): choosing another colour for a group shows at once what applying
// would do to each of its tags: the change, and which rows are ticked. The texts come from the
// form's data attributes, written by the page from its resources. Without this script, "Show these
// colours" does the same on the server.
const form = document.querySelector("form.tag-suggestions-form");
const fill = (template, ...values) => values.reduce((text, value, i) => text.replace(`{${i}}`, value), template ?? "");

if (form) {
  form.querySelector("[data-update]")?.setAttribute("hidden", "");
  for (const select of form.querySelectorAll("select[data-group]")) {
    const section = select.closest("section");
    select.addEventListener("change", () => {
      const colour = select.value;
      const name = select.selectedOptions[0]?.textContent ?? colour;
      select.className = `colour-select ${colour}`;
      for (const row of section?.querySelectorAll("tr[data-tag]") ?? []) {
        const same = row.dataset.current === colour;
        const change = row.querySelector(".change");
        if (change) {
          change.textContent = same ? fill(form.dataset.already, name) : fill(form.dataset.fromTo, row.dataset.currentName, name);
        }
        const box = row.querySelector('input[type="checkbox"]');
        if (box) {
          box.disabled = same;
          box.checked = !same;
        }
        const label = row.querySelector("td.actions label");
        if (label) {
          label.textContent = fill(form.dataset.applyTo, name, row.dataset.tag);
        }
      }
    });
  }
}
