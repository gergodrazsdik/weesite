// Typed confirmations (deleting a post, page or media file, the factory reset; F-0166): Copy beside
// the field puts the text to type on the clipboard, to paste back in. Where the clipboard doesn't
// work the button stays hidden and the text is in the label to type.

import { setUpCopy } from "./clipboard.js";

setUpCopy(document.querySelectorAll(".typed-confirm [data-copy]"), (button) =>
  button.closest(".typed-confirm")?.querySelector(".copy-status"),
);
