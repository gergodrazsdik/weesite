// An error after a save (F-0081): focus moves to its message, so keyboard and screen reader
// users start where the problem is explained, with a link to the field when there is one. Only
// errors move focus; a confirmation such as "Saved." stays where the page put it.
const notice = document.querySelector('.admin-main .notice[role="alert"]');
if (notice) {
  notice.tabIndex = -1;
  notice.focus();
}
