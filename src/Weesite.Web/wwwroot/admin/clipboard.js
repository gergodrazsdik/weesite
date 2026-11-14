// Copy buttons in the admin (F-0120): a button with data-copy="id" copies that field's text (its
// value, or its text) where the clipboard works; without it the button stays hidden and the text
// is there to select. The outcome goes to a status region, so screen readers hear it: a button's
// own text changing under an aria-label isn't announced (F-0117).

const COPIED = "Copied.";
const FAILED = "Couldn't copy: select the text and copy it instead.";

// buttons: the Copy buttons; statusFor: the role="status" element to say the outcome in.
export const setUpCopy = (buttons, statusFor) => {
  if (!navigator.clipboard?.writeText) {
    return;
  }
  for (const button of buttons) {
    const field = document.getElementById(button.dataset.copy);
    if (!field) {
      continue;
    }
    button.hidden = false;
    button.addEventListener("click", async () => {
      const status = statusFor(button);
      try {
        await navigator.clipboard.writeText(field.value ?? field.textContent ?? "");
        if (status) {
          status.textContent = COPIED;
        }
      } catch {
        if (status) {
          status.textContent = FAILED;
        }
      }
    });
  }
};
