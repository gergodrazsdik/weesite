// Signing out clears the unsaved text the editor kept in this browser (F-0089), so the next
// person to sign in here doesn't find it. Text is kept per editor too (Edit.razor), in case the
// browser is left without signing out.
const PREFIX = "weesite:unsaved:";

for (const form of document.querySelectorAll('form[action="/admin/sign-out"]')) {
  form.addEventListener("submit", () => {
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
}
