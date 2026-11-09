// Marks the section being read in "On this page" (ADR 0006): the link to the last heading that
// has reached the top of the window (within READ_LINE pixels) gets aria-current, and at the end of
// the page the last one does. Only an enhancement: without JavaScript the list is a plain list of
// links.

const READ_LINE = 96;

const links = new Map();
for (const link of document.querySelectorAll(".toc a[href^='#']")) {
  const heading = document.getElementById(decodeURIComponent(link.hash.slice(1)));
  if (heading) {
    links.set(heading, link);
  }
}

const headings = [...links.keys()];

const mark = () => {
  const atEnd = window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 2;
  let current = headings[0];
  for (const heading of headings) {
    if (heading.getBoundingClientRect().top <= READ_LINE) {
      current = heading;
    }
  }
  if (atEnd) {
    current = headings.at(-1);
  }
  for (const [heading, link] of links) {
    if (heading === current) {
      link.setAttribute("aria-current", "true");
    } else {
      link.removeAttribute("aria-current");
    }
  }
};

if (headings.length > 0) {
  let pending = false;
  window.addEventListener(
    "scroll",
    () => {
      if (!pending) {
        pending = true;
        requestAnimationFrame(() => {
          pending = false;
          mark();
        });
      }
    },
    { passive: true },
  );
  window.addEventListener("resize", mark);
  mark();
}
