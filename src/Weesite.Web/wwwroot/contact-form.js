// The contact form (ADR 0011): adds the invisible proof of work, and moves focus to an error so
// keyboard and screen reader users start where the problem is explained. Without JavaScript the
// form still sends; the message is then kept as unverified.
import { guard } from "./visitor-proof.js";

const form = document.querySelector("form[data-contact-form]");
if (form instanceof HTMLFormElement) {
  guard(form, "contact");
}

const error = document.querySelector('.contact-page .notice[role="alert"]');
if (error instanceof HTMLElement) {
  error.tabIndex = -1;
  error.focus();
}

// A confirmation present when the page loads isn't announced by its role alone, since live regions
// speak changes: set its text again once the page is there.
const status = document.querySelector('.contact-page .notice[role="status"]');
if (status instanceof HTMLElement && status.textContent?.trim()) {
  const text = status.textContent;
  status.textContent = "";
  setTimeout(() => {
    status.textContent = text;
  }, 250);
}
