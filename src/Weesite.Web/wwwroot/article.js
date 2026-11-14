// A post's or page's text, enhanced (F-0131, F-0132). Without JavaScript the headings and images
// are as rendered.
// - Each section heading gets a link to itself, shown on hover or focus (and always on touch
//   screens), to copy or share the address of a section.
// - An image shown smaller than it is opens at its full size in a dialog, within the window.
// Their words come from the content's data-* attributes, in the site's language (ADR 0008),
// English without them.

const ICON =
  '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"></path><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"></path></svg>';

for (const content of document.querySelectorAll(".content")) {
  const words = {
    headingLink: content.dataset.headingLink ?? "Link to this section",
    enlarge: content.dataset.enlarge ?? "Show at full size",
    close: content.dataset.close ?? "Close",
  };

  for (const heading of content.querySelectorAll(":scope > :is(h2, h3, h4)[id]")) {
    if (heading.querySelector(".heading-link")) {
      continue;
    }
    const link = document.createElement("a");
    link.className = "heading-link";
    link.href = `#${encodeURIComponent(heading.id)}`;
    link.innerHTML = ICON;
    link.setAttribute("aria-label", `${words.headingLink}: ${heading.textContent.trim()}`);
    heading.append(link);
  }

  let dialog = null;
  const open = (image) => {
    if (!dialog) {
      dialog = document.createElement("dialog");
      dialog.className = "image-dialog";
      dialog.innerHTML = '<button type="button" class="btn small close"></button><img alt="">';
      const close = dialog.querySelector("button");
      close.textContent = words.close;
      close.addEventListener("click", () => dialog.close());
      // A click outside the picture, on the backdrop, closes it too; Escape does natively.
      dialog.addEventListener("click", (event) => {
        if (event.target === dialog) {
          dialog.close();
        }
      });
      document.body.append(dialog);
    }
    // src is the stored original; srcset only holds the smaller copies.
    const large = dialog.querySelector("img");
    large.src = image.getAttribute("src");
    large.alt = image.alt;
    dialog.setAttribute("aria-label", image.alt || words.enlarge);
    dialog.showModal();
  };

  // Only images shown at least a fifth smaller than they are, and not already links. A stored
  // image carries its full width in the width attribute (with a srcset, naturalWidth is the width
  // of the copy the browser chose, not of the original).
  const offer = (image) => {
    const width = Number(image.getAttribute("width")) || image.naturalWidth;
    if (image.closest("a, button") || width < image.clientWidth * 1.2) {
      return;
    }
    const button = document.createElement("button");
    button.type = "button";
    // An image placed beside the text (F-0142) keeps its place: the button takes its classes.
    const placed = [...image.classList].filter((name) => ["left", "right", "small", "medium"].includes(name));
    button.className = ["image-zoom", ...placed].join(" ");
    image.classList.remove(...placed);
    button.setAttribute("aria-haspopup", "dialog");
    button.setAttribute("aria-label", image.alt ? `${words.enlarge}: ${image.alt}` : words.enlarge);
    image.replaceWith(button);
    button.append(image);
    button.addEventListener("click", () => open(image));
  };

  for (const image of content.querySelectorAll("img")) {
    if (image.hasAttribute("width") || (image.complete && image.naturalWidth > 0)) {
      offer(image);
    } else {
      image.addEventListener("load", () => offer(image), { once: true });
    }
  }
}
