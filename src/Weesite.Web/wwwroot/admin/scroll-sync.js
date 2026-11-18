// Keeps the Markdown editor's text and its live preview at the same place while either one
// scrolls, in the split layout (F-0180).
//
// The preview's blocks carry data-line, the line of the text each starts on (the server adds it
// for the preview only). Where that line sits in the textarea is measured in a hidden copy laid
// out like it, since a textarea can't say where a wrapped line is. Between two such anchors, the
// panes move in proportion: an image is one line of text but tall in the preview, so for that
// line the preview simply moves further.

// What decides where the textarea's lines wrap, copied to the hidden copy.
const COPIED = [
  "font-family", "font-size", "font-weight", "font-style", "font-stretch", "font-variant-ligatures",
  "line-height", "letter-spacing", "word-spacing", "tab-size", "text-indent", "text-transform",
  "padding-top", "padding-right", "padding-bottom", "padding-left", "overflow-wrap", "word-break",
];

// A pane that's set by the script reports the scroll too; within this many pixels of the value
// set, it's that echo, not the person scrolling.
const ECHO = 2;

/**
 * Lines the two panes up while either scrolls.
 * @param {HTMLTextAreaElement} source The Markdown.
 * @param {HTMLElement} preview The rendered preview, whose blocks have data-line.
 * @param {() => boolean} active Whether the panes are side by side and should follow each other.
 * @returns {{ refresh: () => void }} Call refresh after the preview's content changes.
 */
export function syncScroll(source, preview, active) {
  /** @type {{ src: number, prev: number }[] | null} */
  let anchors = null;
  /** @type {Map<HTMLElement, number>} */
  const expected = new Map();
  // The pane the person scrolled last leads when the layout changes under it.
  let lead = /** @type {HTMLElement} */ (source);

  const mirror = document.createElement("div");
  mirror.className = "md-mirror";
  mirror.setAttribute("aria-hidden", "true");

  const sourceTops = (lines) => {
    const style = getComputedStyle(source);
    for (const name of COPIED) {
      mirror.style.setProperty(name, style.getPropertyValue(name));
    }
    // clientWidth is the textarea's width inside its border, without a scrollbar.
    mirror.style.setProperty("inline-size", `${source.clientWidth}px`);

    const text = source.value;
    const starts = [0];
    for (let at = text.indexOf("\n"); at >= 0; at = text.indexOf("\n", at + 1)) {
      starts.push(at + 1);
    }
    const markers = new Map();
    const parts = [];
    let from = 0;
    for (const line of lines) {
      const start = starts[line - 1];
      if (start === undefined || markers.has(line)) {
        continue;
      }
      parts.push(document.createTextNode(text.slice(from, start)));
      const marker = document.createElement("span");
      markers.set(line, marker);
      parts.push(marker);
      from = start;
    }
    parts.push(document.createTextNode(text.slice(from)));
    mirror.replaceChildren(...parts);
    document.body.append(mirror);
    // An empty span's box is its font's height, centred in the line: half the leading above it is
    // still the line's.
    const origin = mirror.getBoundingClientRect().top;
    const lineHeight = Number.parseFloat(style.lineHeight);
    const tops = new Map([...markers].map(([line, marker]) => {
      const box = marker.getBoundingClientRect();
      const leading = Number.isFinite(lineHeight) ? (lineHeight - box.height) / 2 : 0;
      return [line, box.top - origin - leading];
    }));
    mirror.remove();
    mirror.replaceChildren();
    return tops;
  };

  const measure = () => {
    const blocks = [...preview.querySelectorAll("[data-line]")]
      .map((element) => ({ element, line: Number(element.dataset.line) }))
      .filter(({ line }) => Number.isInteger(line) && line > 0)
      .sort((a, b) => a.line - b.line);
    const tops = sourceTops(blocks.map(({ line }) => line));
    const origin = preview.getBoundingClientRect().top - preview.scrollTop + preview.clientTop;

    // Both positions only ever grow: a block placed beside the text (a floated figure) or a line
    // past the end is left out rather than sending the other pane back.
    const list = [{ src: 0, prev: 0 }];
    for (const { element, line } of blocks) {
      const src = tops.get(line);
      const prev = element.getBoundingClientRect().top - origin;
      const last = list[list.length - 1];
      if (src !== undefined && src > last.src && prev > last.prev) {
        list.push({ src, prev });
      }
    }
    list.push({
      src: Math.max(source.scrollHeight, list[list.length - 1].src + 1),
      prev: Math.max(preview.scrollHeight, list[list.length - 1].prev + 1),
    });
    return list;
  };

  const map = (from, to, y) => {
    anchors ??= measure();
    let i = 0;
    while (i < anchors.length - 2 && anchors[i + 1][from] <= y) {
      i += 1;
    }
    const a = anchors[i];
    const b = anchors[i + 1];
    const t = Math.min(1, Math.max(0, (y - a[from]) / (b[from] - a[from])));
    return a[to] + t * (b[to] - a[to]);
  };

  const follow = (pane) => {
    if (!active()) {
      return;
    }
    const other = pane === source ? preview : source;
    const paneMax = pane.scrollHeight - pane.clientHeight;
    const otherMax = other.scrollHeight - other.clientHeight;
    if (otherMax <= 0) {
      return;
    }
    // At the end of one pane, show the end of the other: the last lines may map short of it.
    const y = paneMax > 0 && pane.scrollTop >= paneMax - 1
      ? otherMax
      : Math.round(pane === source ? map("src", "prev", pane.scrollTop) : map("prev", "src", pane.scrollTop));
    const target = Math.min(otherMax, Math.max(0, y));
    if (Math.abs(other.scrollTop - target) < 1) {
      return;
    }
    expected.set(other, target);
    other.scrollTop = target;
  };

  const onScroll = (pane) => () => {
    const echo = expected.get(pane);
    if (echo !== undefined) {
      expected.delete(pane);
      if (Math.abs(pane.scrollTop - echo) <= ECHO) {
        return;
      }
    }
    lead = pane;
    follow(pane);
  };
  source.addEventListener("scroll", onScroll(source), { passive: true });
  preview.addEventListener("scroll", onScroll(preview), { passive: true });

  // Anything that moves lines or blocks: new text, a new width, an image that finished loading.
  const invalidate = () => {
    anchors = null;
  };
  const realign = () => {
    invalidate();
    follow(lead);
  };
  source.addEventListener("input", invalidate);
  preview.addEventListener("load", realign, true);
  new ResizeObserver(realign).observe(source);
  new ResizeObserver(realign).observe(preview);

  return {
    refresh() {
      // The preview was rendered again, from the text: the text's position leads.
      lead = source;
      realign();
    },
  };
}
