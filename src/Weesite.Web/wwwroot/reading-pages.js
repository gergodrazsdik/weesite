// Reading mode as pages (T-0114): the stylesheet sets a post or page in columns that fill the
// window, and what doesn't fit continues to the right. This turns them a page at a time: the
// buttons and count under the pages, ← and → (and Page Up and Page Down), and the mouse wheel.
// A page is what's in view: the width of the pages plus the gap after the last column. Without
// JavaScript the pages scroll sideways as they are.

const book = document.querySelector(".article-layout > article, main > .wrap > .page");
const bar = document.querySelector(".reading-pages");
const wide = window.matchMedia("screen and (min-width: 64rem)");
const reduce = window.matchMedia("(prefers-reduced-motion: reduce)");

if (book && bar) {
  const status = bar.querySelector("[role='status']");
  const [previous, next] = bar.querySelectorAll("button[data-turn]");

  const gap = () => Number.parseFloat(getComputedStyle(book).columnGap) || 0;
  const step = () => book.clientWidth + gap();
  const sideways = () => wide.matches && book.scrollWidth > book.clientWidth + 1;
  const count = () => Math.max(1, Math.round((book.scrollWidth + gap()) / step()));
  const current = () => Math.min(count(), Math.round(book.scrollLeft / step()) + 1);

  const show = () => {
    bar.hidden = !sideways();
    if (bar.hidden) {
      return;
    }
    const page = current();
    const pages = count();
    status.textContent = bar.dataset.status.replace("{page}", page).replace("{pages}", pages);
    previous.disabled = page <= 1;
    next.disabled = page >= pages;
  };

  const turn = (by) => {
    const page = Math.min(count(), Math.max(1, current() + by));
    book.scrollTo({ left: (page - 1) * step(), behavior: reduce.matches ? "auto" : "smooth" });
  };

  for (const button of [previous, next]) {
    button.addEventListener("click", () => turn(Number(button.dataset.turn)));
  }

  // Keys turn pages unless someone is typing, or a modifier means something else.
  document.addEventListener("keydown", (event) => {
    const typing = event.target.closest("input, textarea, select, [contenteditable]");
    if (!sideways() || typing || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) {
      return;
    }
    const by = { ArrowRight: 1, PageDown: 1, ArrowLeft: -1, PageUp: -1 }[event.key];
    if (by) {
      event.preventDefault();
      turn(by);
    }
  });

  // The wheel turns one page per gesture rather than scrolling a little.
  let resting = true;
  book.addEventListener(
    "wheel",
    (event) => {
      if (!sideways() || Math.abs(event.deltaY) <= Math.abs(event.deltaX)) {
        return;
      }
      event.preventDefault();
      if (resting && Math.abs(event.deltaY) > 4) {
        resting = false;
        turn(Math.sign(event.deltaY));
        setTimeout(() => {
          resting = true;
        }, 450);
      }
    },
    { passive: false },
  );

  let queued = false;
  const later = () => {
    if (!queued) {
      queued = true;
      requestAnimationFrame(() => {
        queued = false;
        show();
      });
    }
  };
  book.addEventListener("scroll", later, { passive: true });

  // A new window size changes how much a page holds: keep the reader on the page they were on.
  let page = 1;
  book.addEventListener("scroll", () => {
    page = current();
  }, { passive: true });
  window.addEventListener("resize", () => {
    book.scrollLeft = (Math.min(page, count()) - 1) * step();
    later();
  });
  wide.addEventListener("change", later);
  window.addEventListener("load", show);
  show();
}
