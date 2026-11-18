// The analytics page (T-0145). The server renders a page that works without this script: the
// per-day table in a <details>, the lists one after another. This adds:
// - the table in a dialog, opened by a button;
// - the lists as tabs (ARIA tabs: arrow keys, Home and End move between them);
// - the report refreshed every 30 s in place: the page is fetched again and its report swapped in,
//   so the scroll position, the chosen tab and the focus stay. Not while the page is hidden or the
//   dialog is open. "Pause updates" stops it (WCAG 2.2.2).
// Texts come from the page's data attributes, written from its resources.

const EVERY = 30_000;
const STORE = "weesite.analytics.tab";

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
  const actions = document.createElement("form");
  actions.method = "dialog";
  actions.className = "dialog-actions";
  const close = document.createElement("button");
  close.className = "btn primary";
  close.type = "submit";
  close.textContent = details.dataset.close ?? "Close";
  actions.append(close);
  dialog.append(title, table, actions);

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

const report = document.querySelector("section.analytics");
if (report) {
  enhance(report);

  const control = document.querySelector(".analytics-refresh");
  const stamp = control?.querySelector(".analytics-updated");
  const pause = control?.querySelector("button");
  const time = () => new Date().toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  const updated = () => {
    if (stamp && control) {
      stamp.textContent = (control.dataset.updated ?? "{0}").replace("{0}", time());
    }
  };
  let paused = false;
  if (control && pause) {
    control.hidden = false;
    updated();
    pause.addEventListener("click", () => {
      paused = !paused;
      pause.textContent = paused ? control.dataset.resume : control.dataset.pause;
    });
  }

  let busy = false;
  const refresh = async () => {
    const current = document.querySelector("section.analytics");
    if (busy || paused || document.hidden || !current || document.querySelector("dialog[open]")) {
      return;
    }
    busy = true;
    try {
      const response = await fetch(location.href, { headers: { Accept: "text/html" }, cache: "no-store", redirect: "manual" });
      if (!response.ok) {
        return;
      }
      const fresh = new DOMParser().parseFromString(await response.text(), "text/html").querySelector("section.analytics");
      // The dialog may have opened while the page was on its way.
      if (!fresh || document.querySelector("dialog[open]")) {
        return;
      }
      const focused = document.activeElement?.id;
      const adopted = document.adoptNode(fresh);
      current.replaceWith(adopted);
      enhance(adopted);
      if (focused) {
        document.getElementById(focused)?.focus({ preventScroll: true });
      }
      updated();
    } catch {
      // Offline or signed out: try again next time.
    } finally {
      busy = false;
    }
  };
  setInterval(refresh, EVERY);
}
