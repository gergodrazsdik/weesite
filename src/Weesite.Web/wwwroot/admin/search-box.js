// The filter boxes on the admin lists (F-0167): clearing a filter with the field's own clear button
// (or Escape) shows the whole list again, as Enter would. Text deleted by typing doesn't, so the page
// doesn't reload while someone types; Enter applies that as before. Without JavaScript, Enter in
// the empty field does it.
for (const input of document.querySelectorAll('form.search-box input[type="search"]')) {
  input.addEventListener("input", (event) => {
    // The clear button and Escape send an input event that isn't typing: no inputType.
    const typed = event instanceof InputEvent && event.inputType !== "";
    if (!typed && input.value === "" && input.defaultValue !== "") {
      input.form?.requestSubmit();
    }
  });
}
