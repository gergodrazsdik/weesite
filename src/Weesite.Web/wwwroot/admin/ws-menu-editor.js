// <ws-menu-editor>: orders the menu page's rows by dragging a row's number, or with Move up and
// Move down buttons (the keyboard and single-pointer way, WCAG 2.5.7). Without JavaScript each row
// has a Position field; this hides those fields and renumbers them from the rows' order, so the
// form posts the same thing either way.
//
// Markup: <ws-menu-editor> around an <ol> whose <li> rows each hold a fieldset with a legend and
// a .menu-position field containing the position input.

class WsMenuEditor extends HTMLElement {
  connectedCallback() {
    this.list = this.querySelector("ol");
    if (!this.list || this.status) {
      return;
    }

    this.status = document.createElement("p");
    this.status.className = "visually-hidden";
    this.status.setAttribute("role", "status");
    this.append(this.status);

    for (const row of this.rows) {
      row.querySelector(".menu-position")?.setAttribute("hidden", "");
      const buttons = document.createElement("div");
      buttons.className = "menu-move";
      buttons.append(this.button("Move up", row, -1), this.button("Move down", row, 1));
      row.querySelector("fieldset")?.append(buttons);

      // An address is used only when the item goes to neither the blog nor a page.
      const target = row.querySelector("select");
      const address = row.querySelector('input[name$=".Href"]');
      if (target && address) {
        const sync = () => {
          address.disabled = target.value !== "";
        };
        target.addEventListener("change", sync);
        sync();
      }
      row.querySelector('input[name$=".Label"]')?.addEventListener("input", () => this.preview());
      this.draggable(row);
    }
    this.list.addEventListener("dragover", (event) => this.dragOver(event));
    this.list.addEventListener("drop", (event) => event.preventDefault());
    this.renumber();
  }

  // The row's number is the grip: the row can be dragged only while it's held, so the fields in
  // it keep working as usual.
  draggable(row) {
    const grip = row.querySelector(".num");
    if (!grip) {
      return;
    }
    grip.classList.add("drag-handle");
    grip.title = "Drag to reorder";
    grip.addEventListener("pointerdown", () => {
      row.draggable = true;
    });
    grip.addEventListener("pointerup", () => {
      row.draggable = false;
    });
    row.addEventListener("dragstart", (event) => {
      if (!row.draggable) {
        event.preventDefault();
        return;
      }
      this.dragging = row;
      row.classList.add("dragging");
      event.dataTransfer.effectAllowed = "move";
      event.dataTransfer.setData("text/plain", "");
    });
    row.addEventListener("dragend", () => {
      row.draggable = false;
      row.classList.remove("dragging");
      this.dragging = null;
      this.renumber();
      this.preview();
      this.announce(row);
    });
  }

  // While dragging, the row moves to where the pointer is: above a row's middle goes before it.
  dragOver(event) {
    const row = this.dragging;
    if (!row) {
      return;
    }
    event.preventDefault();
    event.dataTransfer.dropEffect = "move";
    const over = event.target.closest?.(".menu-rows > li");
    if (!over || over === row) {
      return;
    }
    const box = over.getBoundingClientRect();
    if (event.clientY < box.top + box.height / 2) {
      over.before(row);
    } else {
      over.after(row);
    }
  }

  announce(row) {
    const label = row.querySelector("input")?.value || row.querySelector("legend")?.textContent || "Item";
    this.status.textContent = `${label}: position ${this.rows.indexOf(row) + 1} of ${this.rows.length}`;
  }

  // The preview shows the labels in their current order, as the header will.
  preview() {
    const list = this.querySelector(".menu-preview ul");
    if (!list) {
      return;
    }
    list.replaceChildren(
      ...this.rows
        .map((row) => row.querySelector('input[name$=".Label"]')?.value.trim() ?? "")
        .filter(Boolean)
        .map((label) => {
          const li = document.createElement("li");
          li.textContent = label;
          return li;
        }),
    );
  }

  get rows() {
    return [...this.list.children];
  }

  button(text, row, step) {
    const button = document.createElement("button");
    button.type = "button";
    // An arrow for the eye, the name for assistive technology.
    button.innerHTML = `<span aria-hidden="true">${step < 0 ? "↑" : "↓"}</span><span class="visually-hidden">${text}</span>`;
    button.dataset.step = String(step);
    button.addEventListener("click", () => this.move(row, step, button));
    return button;
  }

  move(row, step, button) {
    const rows = this.rows;
    const index = rows.indexOf(row);
    const target = rows[index + step];
    if (!target) {
      return;
    }
    if (step < 0) {
      target.before(row);
    } else {
      target.after(row);
    }
    this.renumber();
    this.preview();
    button.focus();
    this.announce(row);
  }

  // Positions and the numbers shown follow the rows' order; the first row can't move up, the last
  // can't move down.
  renumber() {
    const rows = this.rows;
    rows.forEach((row, index) => {
      const position = row.querySelector(".menu-position input");
      if (position) {
        position.value = String(index + 1);
      }
      const number = row.querySelector(".num");
      if (number) {
        number.textContent = String(index + 1);
      }
      for (const button of row.querySelectorAll(".menu-move button")) {
        const step = Number(button.dataset.step);
        button.disabled = (step < 0 && index === 0) || (step > 0 && index === rows.length - 1);
      }
    });
  }
}

customElements.define("ws-menu-editor", WsMenuEditor);
