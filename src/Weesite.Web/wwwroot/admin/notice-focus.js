// An error after a save (F-0081): focus moves to its message, so keyboard and screen reader
// users start where the problem is explained, with a link to the field when there is one. Only
// errors move focus; a confirmation such as "Saved." stays where the page put it.
const notice = document.querySelector('.admin-main .notice[role="alert"]');
if (notice) {
  notice.tabIndex = -1;
  notice.focus();
}

// A confirmation present when the page loads ("Saved." after a save) isn't announced by its role
// alone, since live regions speak changes: set its text again once the page is there (F-0091 L29).
for (const status of document.querySelectorAll('.admin-main .notice[role="status"]')) {
  const text = status.textContent;
  if (!text?.trim() || status.children.length > 0) {
    continue;
  }
  status.textContent = "";
  setTimeout(() => {
    status.textContent = text;
  }, 250);
}
