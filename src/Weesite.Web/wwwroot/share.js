// <ws-share>: the Share button on posts (T-0075). The server renders the button hidden and the
// dialog closed, with LinkedIn, copy the link and email; this shows the button, opens the dialog
// as a modal (focus stays inside, Escape closes it and focus goes back to the button), closes it
// on a click outside, copies the link, and offers the device's own share sheet where there is one.
// Without JavaScript there is no button.

class WsShare extends HTMLElement {
  connectedCallback() {
    const open = this.querySelector(".share-open");
    const dialog = this.querySelector("dialog");
    if (!open || !dialog || typeof dialog.showModal !== "function" || this.dataset.ready) {
      return;
    }
    this.dataset.ready = "true";
    const status = dialog.querySelector(".share-status");
    const say = (text) => {
      if (status) {
        status.textContent = text;
      }
    };

    open.hidden = false;
    open.addEventListener("click", () => dialog.showModal());
    // Other buttons on the page that open this dialog, such as the one by the breadcrumbs
    // (F-0122). The dialog gives the focus back to whichever opened it.
    for (const other of document.querySelectorAll("[data-share-open]")) {
      other.hidden = false;
      other.addEventListener("click", () => dialog.showModal());
    }
    dialog.querySelector(".share-close")?.addEventListener("click", () => dialog.close());
    // The dialog has no padding of its own, so a click on it, not on its body, is on the backdrop.
    dialog.addEventListener("click", (event) => {
      if (event.target === dialog) {
        dialog.close();
      }
    });
    dialog.addEventListener("close", () => say(""));

    const copy = dialog.querySelector(".share-copy");
    if (copy && navigator.clipboard) {
      copy.addEventListener("click", async () => {
        try {
          await navigator.clipboard.writeText(this.dataset.url ?? location.href);
          say(this.dataset.copied ?? "");
        } catch {
          say(this.dataset.copyFailed ?? "");
        }
      });
    } else {
      copy?.closest("li")?.setAttribute("hidden", "");
    }

    const native = dialog.querySelector(".share-native");
    const data = { title: this.dataset.title, url: this.dataset.url };
    if (native && navigator.share && (!navigator.canShare || navigator.canShare(data))) {
      native.closest("li").hidden = false;
      native.addEventListener("click", async () => {
        try {
          await navigator.share(data);
          dialog.close();
        } catch {
          // Cancelled, or not allowed: the other options are still there.
        }
      });
    }
  }
}

if (!customElements.get("ws-share")) {
  customElements.define("ws-share", WsShare);
}
