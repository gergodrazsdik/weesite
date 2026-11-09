// Shows a tag's new colour on its label and on the select as soon as it's chosen on the tags page.
for (const select of document.querySelectorAll("select[data-colour-for]")) {
  const label = document.querySelector(`[data-preview="${select.dataset.colourFor}"]`);
  if (!label) {
    continue;
  }
  select.addEventListener("change", () => {
    label.className = select.value ? `tag ${select.value}` : "tag";
    select.className = select.value ? `colour-select ${select.value}` : "colour-select";
  });
}
