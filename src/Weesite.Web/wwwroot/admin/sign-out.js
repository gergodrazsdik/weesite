// Signing out clears the unsaved text the editor kept in this browser (F-0089), so the next
// person to sign in here doesn't find it. Text is kept per editor too (Edit.razor), in case the
// browser is left without signing out.
const PREFIX = "weesite:unsaved:";
const ACTIONS = new Set(["/admin/sign-out", "/admin/sign-out-everywhere"]);

// On the document, so it runs after the form's own listeners: a sign-out the editor cancels
// (confirm-forms.js) clears nothing.
document.addEventListener("submit", (event) => {
  const form = event.target;
  if (event.defaultPrevented || !(form instanceof HTMLFormElement) || !ACTIONS.has(form.getAttribute("action"))) {
    return;
  }
  try {
    const keys = [];
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (key?.startsWith(PREFIX)) {
        keys.push(key);
      }
    }
    for (const key of keys) {
      localStorage.removeItem(key);
    }
  } catch {
    // Storage can be missing or blocked; then there's nothing kept to clear.
  }
});
