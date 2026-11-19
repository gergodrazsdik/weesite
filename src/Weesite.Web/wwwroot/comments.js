// <ws-comments>: the comment form and the writer's own comments (ADR 0011). The server renders it
// hidden; this shows it once it knows the post takes comments, with a fresh form token, and sends
// a comment as JSON with a proof of work, solved in the background from the first focus. A new
// comment's token comes back once: it's kept in localStorage ("weesite:comments": comment ID to
// the post's key and the token), and sent in a header to read, edit or delete that comment. The
// browser shows its own comments that wait for moderation, and Edit and Delete on its own
// published ones. Texts come from the element's data attributes, written from the site's resources.

import { AnswerSupply } from "./visitor-proof.js";

const STORE = "weesite:comments";

const readStore = () => {
  try {
    const parsed = JSON.parse(localStorage.getItem(STORE) ?? "{}");
    return parsed && typeof parsed === "object" ? parsed : {};
  } catch {
    return {};
  }
};

const writeStore = (change) => {
  try {
    const all = readStore();
    change(all);
    localStorage.setItem(STORE, JSON.stringify(all));
  } catch {
    // Storage refused (private mode, full): the comment still counts, only this browser forgets it.
  }
};

const element = (tag, className, text) => {
  const node = document.createElement(tag);
  if (className) {
    node.className = className;
  }
  if (text !== undefined) {
    node.textContent = text;
  }
  return node;
};

class WsComments extends HTMLElement {
  connectedCallback() {
    if (this.dataset.ready) {
      return;
    }
    this.dataset.ready = "true";
    this.start();
  }

  async start() {
    this.post = this.dataset.post ?? "";
    this.key = this.dataset.key ?? this.post;
    this.form = this.querySelector("form[data-comment-form]");
    this.own = this.querySelector("[data-own]");
    this.status = this.querySelector("[data-status]");
    this.error = this.querySelector("[data-error]");
    this.supply = new AnswerSupply("comment");
    if (!this.form || !this.own) {
      return;
    }

    const open = await this.renewToken();
    this.form.hidden = !open;
    this.hidden = false;
    this.form.addEventListener("focusin", () => this.supply.prepare(), { once: true });
    this.form.addEventListener("submit", (event) => {
      event.preventDefault();
      this.send();
    });
    await this.showOwn();
  }

  url(id) {
    const path = id ? `/visitor/comments/${encodeURIComponent(id)}` : "/visitor/comments";
    return `${path}?post=${encodeURIComponent(this.post)}`;
  }

  say(text) {
    if (this.status) {
      this.status.textContent = text ?? "";
    }
  }

  showError(text, field) {
    if (!this.error) {
      return;
    }
    this.error.textContent = text ?? "";
    this.error.hidden = !text;
    for (const input of this.form.querySelectorAll("input, textarea")) {
      input.removeAttribute("aria-invalid");
    }
    if (field) {
      field.setAttribute("aria-invalid", "true");
      field.focus();
    }
  }

  // A fresh form token: the minimum time counts from when the form shows, not from a cached page.
  async renewToken() {
    try {
      const response = await fetch(`/visitor/comments/form?post=${encodeURIComponent(this.post)}`, {
        headers: { Accept: "application/json" },
        cache: "no-store",
      });
      if (!response.ok) {
        return false;
      }
      const answer = await response.json();
      this.formToken = answer.token ?? null;
      return Boolean(answer.open);
    } catch {
      return false;
    }
  }

  async send() {
    if (this.sending) {
      return;
    }
    this.sending = true;
    const button = this.form.querySelector("button[type=submit]");
    button?.setAttribute("aria-disabled", "true");
    this.showError("");
    this.say(this.dataset.sending);
    const name = this.form.elements.namedItem("name");
    const text = this.form.elements.namedItem("text");
    const website = this.form.elements.namedItem("website");
    try {
      let proof = null;
      try {
        proof = await this.supply.take();
      } catch {
        // No answer: the server says what to do.
      }
      const response = await fetch(this.url(), {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify({
          post: this.post,
          name: name?.value ?? "",
          text: text?.value ?? "",
          formToken: this.formToken,
          website: website?.value ?? "",
          proof,
        }),
      });
      const answer = await response.json().catch(() => ({}));
      if (!response.ok) {
        this.say("");
        const field = answer.error === "bad-name" ? name : answer.error === "bad-text" ? text : null;
        this.showError(answer.message ?? this.dataset.failed, field);
        if (answer.error === "expired" || answer.error === "invalid") {
          await this.renewToken();
        }
        return;
      }

      writeStore((all) => {
        all[answer.id] = { key: this.key, token: answer.token };
      });
      if (text) {
        text.value = "";
      }
      await this.renewToken();
      this.say(this.dataset.sent);
      await this.showOwn();
    } catch {
      this.say("");
      this.showError(this.dataset.failed);
    } finally {
      this.sending = false;
      button?.removeAttribute("aria-disabled");
    }
  }

  // This browser's comments on this post: waiting ones in their own list, published ones marked.
  async showOwn() {
    this.own.replaceChildren();
    for (const old of this.closest("section")?.querySelectorAll(".own-controls, .own-note") ?? []) {
      old.remove();
    }
    const mine = Object.entries(readStore()).filter(([, entry]) => entry?.key === this.key && entry.token);
    for (const [id, entry] of mine) {
      let comment;
      try {
        const response = await fetch(this.url(id), {
          headers: { Accept: "application/json", Authorization: `Comment ${entry.token}` },
          cache: "no-store",
        });
        if (response.status === 404 || response.status === 403) {
          writeStore((all) => delete all[id]);
          continue;
        }
        if (!response.ok) {
          continue;
        }
        comment = await response.json();
      } catch {
        continue;
      }

      const published = comment.status === "approved" ? document.getElementById(`comment-${id}`) : null;
      if (published) {
        if (comment.pendingText) {
          published.append(element("p", "own-note", this.dataset.editWaiting));
        }
        published.append(this.controls(comment, entry.token));
        continue;
      }

      const article = element("article", "comment own");
      article.id = `own-${id}`;
      const meta = element("p", "comment-meta");
      meta.append(element("span", "comment-name", comment.name), element("span", "own-label", this.dataset.yours));
      const note = comment.status === "rejected"
        ? this.dataset.rejected
        : comment.status === "approved" && comment.pendingText ? this.dataset.editWaiting : this.dataset.waiting;
      article.append(meta, element("p", "comment-text", comment.pendingText ?? comment.text), element("p", "own-note", note));
      article.append(this.controls(comment, entry.token));
      this.own.append(article);
    }
  }

  controls(comment, token) {
    const box = element("div", "own-controls");
    const edit = element("button", "btn small", this.dataset.edit);
    edit.type = "button";
    const remove = element("button", "btn small danger", this.dataset.delete);
    remove.type = "button";
    if (comment.status !== "rejected") {
      box.append(edit);
    }
    box.append(remove);

    edit.addEventListener("click", () => this.edit(comment, token, box));
    remove.addEventListener("click", () => {
      // Two steps instead of a dialog: Delete, then Delete for good or Cancel.
      const really = element("button", "btn small danger", this.dataset.deleteConfirm);
      really.type = "button";
      const cancel = element("button", "btn small", this.dataset.cancel);
      cancel.type = "button";
      box.replaceChildren(really, cancel);
      really.focus();
      cancel.addEventListener("click", () => this.showOwn());
      really.addEventListener("click", () => this.remove(comment, token));
    });
    return box;
  }

  edit(comment, token, box) {
    const form = element("form", "own-edit");
    const label = element("label", null, this.dataset.editLabel);
    const area = element("textarea");
    area.id = `edit-${comment.id}`;
    label.htmlFor = area.id;
    area.required = true;
    area.rows = 5;
    area.maxLength = 2000;
    area.value = comment.pendingText ?? comment.text;
    const save = element("button", "btn small primary", this.dataset.save);
    save.type = "submit";
    const cancel = element("button", "btn small", this.dataset.cancel);
    cancel.type = "button";
    const actions = element("div", "own-controls");
    actions.append(save, cancel);
    form.append(label, area, actions);
    box.replaceWith(form);
    area.focus();
    this.supply.prepare();

    cancel.addEventListener("click", () => this.showOwn());
    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      this.say(this.dataset.sending);
      try {
        let proof = null;
        try {
          proof = await this.supply.take();
        } catch {
          // The server says what to do.
        }
        const response = await fetch(this.url(comment.id), {
          method: "PUT",
          headers: { "Content-Type": "application/json", Accept: "application/json", Authorization: `Comment ${token}` },
          body: JSON.stringify({ text: area.value, proof }),
        });
        const answer = await response.json().catch(() => ({}));
        if (!response.ok) {
          this.say(answer.message ?? this.dataset.failed);
          area.setAttribute("aria-invalid", "true");
          area.focus();
          return;
        }
        this.say(this.dataset.saved);
        await this.showOwn();
      } catch {
        this.say(this.dataset.failed);
      }
    });
  }

  async remove(comment, token) {
    try {
      const response = await fetch(this.url(comment.id), {
        method: "DELETE",
        headers: { Accept: "application/json", Authorization: `Comment ${token}` },
      });
      if (!response.ok && response.status !== 404 && response.status !== 403) {
        this.say(this.dataset.failed);
        return;
      }
    } catch {
      this.say(this.dataset.failed);
      return;
    }

    writeStore((all) => delete all[comment.id]);
    document.getElementById(`comment-${comment.id}`)?.closest("li")?.remove();
    this.say(this.dataset.deleted);
    await this.showOwn();
    this.form?.querySelector("input, textarea")?.focus();
  }
}

if (!customElements.get("ws-comments")) {
  customElements.define("ws-comments", WsComments);
}
