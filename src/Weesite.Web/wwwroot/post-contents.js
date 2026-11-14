// Marks the section being read in "On this page" (ADR 0006): the links to the last heading that
// has reached the top of the window (within READ_LINE pixels) get aria-current, and at the end of
// the page the last one does, in the side column and in the list at the top (F-0133). That list,
// open over the text, closes once a section is chosen, on Escape, or on a click outside it. Only
// an enhancement: without JavaScript the lists are plain lists of links.

const READ_LINE = 96;

const links = new Map();
for (const link of document.querySelectorAll(".toc a[href^='#']")) {
  const heading = document.getElementById(decodeURIComponent(link.hash.slice(1)));
  if (heading) {
    links.set(heading, [...(links.get(heading) ?? []), link]);
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
  for (const [heading, list] of links) {
    for (const link of list) {
      if (heading === current) {
        link.setAttribute("aria-current", "true");
      } else {
        link.removeAttribute("aria-current");
      }
    }
  }
};

for (const menu of document.querySelectorAll("details.toc-top")) {
  const summary = menu.querySelector("summary");
  menu.addEventListener("click", (event) => {
    if (event.target.closest("a")) {
      menu.open = false;
    }
  });
  menu.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && menu.open) {
      menu.open = false;
      summary.focus();
    }
  });
  document.addEventListener("click", (event) => {
    if (menu.open && !menu.contains(event.target)) {
      menu.open = false;
    }
  });
}

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
