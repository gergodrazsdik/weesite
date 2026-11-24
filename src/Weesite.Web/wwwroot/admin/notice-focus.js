// The focus goes to what a post's answer says (F-0081, U-31): an error, with a link to the field
// when there is one, or else a notice marked data-focus, such as "Saved." after a save or what an
// action on Media did. Keyboard and screen reader users start where the page says what happened,
// not at its top, and the focus reads the notice. The editor's "Saved." is focused by
// editor-form.js.
const notice = document.querySelector('.admin-main .notice[role="alert"]') ?? document.querySelector(".admin-main .notice[data-focus]");
if (notice instanceof HTMLElement) {
  notice.tabIndex = -1;
  notice.focus();
}

// Any other confirmation present when the page loads isn't announced by its role alone, since live
// regions speak changes: set its text again once the page is there (F-0091 L29). Not the focused
// one: the focus reads it already, and emptying it would have it read empty, or twice.
for (const status of document.querySelectorAll('.admin-main .notice[role="status"]')) {
  const text = status.textContent;
  if (status === notice || status.hasAttribute("data-focus") || !text?.trim() || status.children.length > 0) {
    continue;
  }
  status.textContent = "";
  setTimeout(() => {
    status.textContent = text;
  }, 250);
}
