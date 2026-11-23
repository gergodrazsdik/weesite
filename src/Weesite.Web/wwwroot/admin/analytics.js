// The analytics page (T-0145, F-0202). The page comes with a skeleton where the report goes
// (section.analytics, aria-busy, data-report: the report's own address). This:
// - loads the report into it at once, so the page shows before its numbers are read;
// - shows the per-day table in a dialog, opened by a button (the report has it in a <details>);
// - shows the lists as tabs (ARIA tabs: arrow keys, Home and End move between them);
// - loads the report again every 30 s and swaps it in, so the scroll position, the chosen tab and
//   the focus stay (on elements without an id too: F-0223). Not while the page is hidden or the
//   dialog is open; a page shown again updates at once if it missed one. "Pause updates" stops it
//   (WCAG 2.2.2), on the other ranges too, until it's resumed or the browser tab is closed.
// - says so when updates keep failing, and keeps trying.
// Texts come from the page's data attributes, written from its resources.

const EVERY = 30_000;
const STORE = "weesite.analytics.tab";
const PAUSED = "weesite.analytics.paused";

const remembered = () => {
  try {
    return sessionStorage.getItem(STORE);
  } catch {
    return null;
  }
};

const remember = (id) => {
  try {
    sessionStorage.setItem(STORE, id);
  } catch {
    // Storage refused: the first tab after a reload.
  }
};

// Paused stays paused when the range changes, which loads the page again (F-0203).
const wasPaused = () => {
  try {
    return sessionStorage.getItem(PAUSED) === "true";
  } catch {
    return false;
  }
};

const keepPaused = (paused) => {
  try {
    if (paused) {
      sessionStorage.setItem(PAUSED, "true");
    } else {
      sessionStorage.removeItem(PAUSED);
    }
  } catch {
    // Storage refused: the next page refreshes again.
  }
};

// The per-day table: a button opens it in a dialog; Escape or Close shut it, focus goes back.
function tableInDialog(report) {
  const details = report.querySelector("details.as-table");
  const summary = details?.querySelector("summary");
  const table = details?.querySelector(".table-scroll");
  if (!details || !summary || !table) {
    return;
  }
  const dialog = document.createElement("dialog");
  dialog.className = "code-dialog views-dialog";
  dialog.id = "views-per-day-dialog";
  dialog.setAttribute("aria-labelledby", "views-per-day-title");
  const title = document.createElement("h2");
  title.id = "views-per-day-title";
  title.textContent = details.dataset.dialogTitle ?? summary.textContent;
  // A close button beside the title as well, so a long table needn't be scrolled to close it
  // (F-0184), as in the media picker. It comes first, so it has the focus when the dialog opens.
  const head = document.createElement("div");
  head.className = "dialog-head";
  const top = document.createElement("form");
  top.method = "dialog";
  const cross = document.createElement("button");
  cross.className = "close";
  cross.type = "submit";
  cross.textContent = "×";
  cross.setAttribute("aria-label", details.dataset.close ?? "Close");
  top.append(cross);
  head.append(title, top);
  const actions = document.createElement("form");
  actions.method = "dialog";
  actions.className = "dialog-actions";
  const close = document.createElement("button");
  close.className = "btn primary";
  close.type = "submit";
  close.textContent = details.dataset.close ?? "Close";
  actions.append(close);
  dialog.append(head, table, actions);

  const open = document.createElement("button");
  open.type = "button";
  open.className = "btn as-table-button";
  open.id = "views-per-day-button";
  open.textContent = summary.textContent;
  open.addEventListener("click", () => dialog.showModal());
  dialog.addEventListener("close", () => open.focus());
  details.replaceWith(open, dialog);
}

// The lists as tabs, each section a panel; its heading stays for the panel's name.
function tabs(report, chosen) {
  const lists = report.querySelector("[data-tabs]");
  const panels = [...(lists?.querySelectorAll(":scope > section") ?? [])];
  if (!lists || panels.length < 2) {
    return;
  }
  const tablist = document.createElement("div");
  tablist.setAttribute("role", "tablist");
  tablist.setAttribute("aria-label", lists.dataset.tabs ?? "");
  tablist.className = "report-tabs";
  const buttons = panels.map((panel, i) => {
    const heading = panel.querySelector("h2");
    const tab = document.createElement("button");
    tab.type = "button";
    tab.setAttribute("role", "tab");
    tab.id = `${heading?.id ?? `report-${i}`}-tab`;
    tab.textContent = heading?.textContent ?? "";
    panel.id ||= `${tab.id}-panel`;
    tab.setAttribute("aria-controls", panel.id);
    panel.setAttribute("role", "tabpanel");
    panel.setAttribute("aria-labelledby", tab.id);
    panel.tabIndex = 0;
    heading?.classList.add("visually-hidden");
    tablist.append(tab);
    return tab;
  });
  const select = (index, focus) => {
    buttons.forEach((tab, i) => {
      const selected = i === index;
      tab.setAttribute("aria-selected", String(selected));
      tab.tabIndex = selected ? 0 : -1;
      panels[i].hidden = !selected;
    });
    remember(buttons[index].id);
    if (focus) {
      buttons[index].focus();
    }
  };
  tablist.addEventListener("click", (event) => {
    const index = buttons.indexOf(event.target.closest?.('[role="tab"]'));
    if (index >= 0) {
      select(index, false);
    }
  });
  tablist.addEventListener("keydown", (event) => {
    const current = buttons.indexOf(document.activeElement);
    const next = { ArrowRight: current + 1, ArrowLeft: current - 1, Home: 0, End: buttons.length - 1 }[event.key];
    if (current < 0 || next === undefined) {
      return;
    }
    event.preventDefault();
    select((next + buttons.length) % buttons.length, true);
  });
  lists.classList.add("tabbed");
  lists.before(tablist);
  const index = buttons.findIndex((tab) => tab.id === chosen);
  select(index >= 0 ? index : 0, false);
}

function enhance(report) {
  tableInDialog(report);
  tabs(report, remembered());
}

const placeholder = document.querySelector("section.analytics[data-report]");
const source = placeholder?.dataset.report;

// The elements the focus can be on, in a report.
const FOCUSABLE = "a[href], button, input, select, textarea, summary, [tabindex]";
const signature = (element) =>
  `${element.tagName}|${element.getAttribute("href") ?? ""}|${element.getAttribute("role") ?? ""}|${element.className}`;
const alike = (scope, sign) => [...scope.querySelectorAll(FOCUSABLE)].filter((element) => signature(element) === sign);

// Where the focus is in the report, as a key that finds the same place in a fresh one (F-0223):
// its id; without one, the nearest element around it with an id and which of the elements like
// it in there it is (a table's scroll region, the DB-IP link, a panel). Null when it's elsewhere.
function focusKey(report) {
  const focused = document.activeElement;
  if (!(focused instanceof HTMLElement) || focused === report || !report.contains(focused)) {
    return null;
  }
  if (focused.id) {
    return { id: focused.id };
  }
  const around = focused.parentElement?.closest("[id]");
  const scope = around && report.contains(around) ? around : report;
  const sign = signature(focused);
  return { scope: scope === report ? null : scope.id, sign, index: alike(scope, sign).indexOf(focused) };
}

// Puts the focus back where the key says; on the place around it when that's gone, if it can take it.
function restoreFocus(report, key) {
  if (!key) {
    return;
  }
  let target = null;
  if (key.id) {
    target = document.getElementById(key.id);
  } else {
    const scope = key.scope ? document.getElementById(key.scope) : report;
    if (scope && report.contains(scope)) {
      const like = alike(scope, key.sign);
      target = like[Math.min(key.index, like.length - 1)] ?? (scope.tabIndex >= 0 ? scope : null);
    }
  }
  target?.focus({ preventScroll: true });
}

// The report from its address, swapped in for the one shown: true when it was, false when the
// dialog opened meanwhile. Throws when it didn't come, such as when the sign-in expired (the
// redirect isn't followed, so it isn't OK).
async function load() {
  const current = document.querySelector("section.analytics");
  if (!source || !current) {
    throw new Error("No report on this page.");
  }
  const response = await fetch(source, { headers: { Accept: "text/html" }, cache: "no-store", redirect: "manual" });
  if (!response.ok) {
    throw new Error(`The report answered ${response.status}.`);
  }
  const fresh = new DOMParser().parseFromString(await response.text(), "text/html").querySelector("section.analytics");
  if (!fresh) {
    throw new Error("The answer has no report.");
  }
  // The dialog may have opened while the report was on its way.
  if (document.querySelector("dialog[open]")) {
    return false;
  }
  const focused = focusKey(current);
  const adopted = document.adoptNode(fresh);
  current.replaceWith(adopted);
  enhance(adopted);
  restoreFocus(adopted, focused);
  return true;
}

async function start() {
  try {
    await load();
  } catch {
    if (placeholder) {
      const note = document.createElement("p");
      note.className = "notice error";
      note.setAttribute("role", "alert");
      note.textContent = placeholder.dataset.failed ?? "";
      placeholder.replaceChildren(note);
      placeholder.removeAttribute("aria-busy");
      placeholder.classList.remove("loading");
    }
    return;
  }

  const control = document.querySelector(".analytics-refresh");
  const stamp = control?.querySelector(".analytics-updated");
  const status = control?.querySelector(".analytics-status");
  const pause = control?.querySelector("button");
  const time = () => new Date().toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  let last = Date.now();
  let failures = 0;
  const updated = () => {
    last = Date.now();
    if (stamp && control) {
      stamp.textContent = (control.dataset.updated ?? "{0}").replace("{0}", time());
    }
  };
  // Failed updates say so after the second in a row (one can be a blip), and the tries go on;
  // the next that comes clears it (F-0223).
  const failed = (count) => {
    failures = count;
    if (status && control) {
      status.textContent = failures >= 2 ? (control.dataset.refreshFailed ?? "") : "";
    }
  };
  let paused = wasPaused();
  if (control && pause) {
    control.hidden = false;
    updated();
    pause.textContent = paused ? control.dataset.resume : control.dataset.pause;
    pause.addEventListener("click", () => {
      paused = !paused;
      keepPaused(paused);
      pause.textContent = paused ? control.dataset.resume : control.dataset.pause;
    });
  }

  let busy = false;
  const refresh = async () => {
    if (busy || paused || document.hidden || document.querySelector("dialog[open]")) {
      return;
    }
    busy = true;
    try {
      if (await load()) {
        updated();
        failed(0);
      }
    } catch {
      failed(failures + 1);
    } finally {
      busy = false;
    }
  };
  setInterval(refresh, EVERY);

  // A tab coming back from the background updates at once when it missed one (F-0223).
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden && Date.now() - last >= EVERY) {
      refresh();
    }
  });
}

if (source) {
  start();
}
