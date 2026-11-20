// A form that posts goes once (F-0201): from the moment it's sent until the next page arrives, a
// second press of its button, or of Enter, does nothing, so a slow action such as the site's reset
// can't run twice. The buttons say so with aria-disabled and the form with aria-busy; they aren't
// disabled, as a disabled button leaves its name and value out of what the form sends. A form a
// script sends itself (it cancels the submit event, as the comment form does) is left to it. A form
// that downloads a file (data-download) stays on the page, so it opens again after a few seconds;
// coming back to a page from the browser's history opens its forms again too.

const AGAIN_AFTER = 3000;

const open = (form) => {
  delete form.dataset.sending;
  form.removeAttribute("aria-busy");
  for (const button of form.querySelectorAll("[data-sending-button]")) {
    button.removeAttribute("aria-disabled");
    delete button.dataset.sendingButton;
  }
};

// Last in the bubbling order, so a form's own listeners have had their say.
document.addEventListener("submit", (event) => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement) || form.method !== "post") {
    return;
  }
  if (form.dataset.sending) {
    event.preventDefault();
    return;
  }
  if (event.defaultPrevented || (form.target && form.target !== "_self")) {
    return;
  }
  form.dataset.sending = "true";
  form.setAttribute("aria-busy", "true");
  for (const button of form.querySelectorAll('button:not([type]), button[type="submit"], input[type="submit"]')) {
    if (!button.hasAttribute("aria-disabled")) {
      button.setAttribute("aria-disabled", "true");
      button.dataset.sendingButton = "true";
    }
  }
  if (form.hasAttribute("data-download")) {
    setTimeout(() => open(form), AGAIN_AFTER);
  }
});

window.addEventListener("pageshow", (event) => {
  if (event.persisted) {
    for (const form of document.querySelectorAll("form[data-sending]")) {
      open(form);
    }
  }
});
