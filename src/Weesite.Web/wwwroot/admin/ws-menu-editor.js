// <ws-menu-editor>: orders the menu page's rows with Move up and Move down buttons. Without
// JavaScript each row has a Position field; this hides those fields and renumbers them from the
// rows' order, so the form posts the same thing either way.
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
    }
    this.renumber();
  }

  get rows() {
    return [...this.list.children];
  }

  button(text, row, step) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = text;
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
    button.focus();
    const label = row.querySelector("input")?.value || row.querySelector("legend")?.textContent || "Item";
    this.status.textContent = `${label}: position ${this.rows.indexOf(row) + 1} of ${this.rows.length}`;
  }

  // Positions follow the rows' order; the first row can't move up, the last can't move down.
  renumber() {
    const rows = this.rows;
    rows.forEach((row, index) => {
      const position = row.querySelector(".menu-position input");
      if (position) {
        position.value = String(index + 1);
      }
      for (const button of row.querySelectorAll(".menu-move button")) {
        const step = Number(button.dataset.step);
        button.disabled = (step < 0 && index === 0) || (step > 0 && index === rows.length - 1);
      }
    });
  }
}

customElements.define("ws-menu-editor", WsMenuEditor);
