// <ws-theme-switch>: the System / Light / Dark switch without a page reload (brief, "Admin UI
// from web components"; T-0047). It wraps the switch's form: a click posts the same form in the
// background (the server sets the theme cookie and answers 204), then the page's data-theme and
// the buttons' aria-pressed follow. Without JavaScript, or if the request fails, the form posts
// as usual and the page reloads in the chosen mode.

class WsThemeSwitch extends HTMLElement {
  connectedCallback() {
    const form = this.querySelector("form");
    if (!form || this.dataset.ready) {
      return;
    }
    this.dataset.ready = "true";
    form.addEventListener("submit", async (event) => {
      const button = event.submitter;
      if (!button?.value || form.dataset.fallback) {
        return;
      }
      event.preventDefault();
      const body = new FormData(form, button);
      try {
        const response = await fetch(form.action, {
          method: "POST",
          body,
          credentials: "same-origin",
          headers: { Accept: "application/json" },
        });
        if (!response.ok) {
          throw new Error(String(response.status));
        }
      } catch {
        // Let the form do it the plain way.
        form.dataset.fallback = "true";
        form.requestSubmit(button);
        return;
      }
      this.apply(button.value);
    });
  }

  apply(mode) {
    const root = document.documentElement;
    if (mode === "light" || mode === "dark") {
      root.dataset.theme = mode;
    } else {
      delete root.dataset.theme;
    }
    // Every switch on the page (the admin has one in the sidebar, the site one in the header).
    for (const button of document.querySelectorAll("ws-theme-switch button[name='mode']")) {
      button.setAttribute("aria-pressed", String(button.value === mode));
    }
  }
}

customElements.define("ws-theme-switch", WsThemeSwitch);
