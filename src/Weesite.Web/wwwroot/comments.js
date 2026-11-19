// <ws-comments>: the comment form, the writer's own comments and what a post's comments do with
// JavaScript (ADR 0011, T-0161). The server renders the form hidden; this shows it once it knows the
// post takes comments, with a fresh form token, and sends a comment as JSON with a proof of work,
// solved in the background from the first focus. A new comment's token comes back once: it's kept in
// localStorage ("weesite:comments": comment ID to the post's key and the token), and sent in a header
// to read, edit or delete that comment. The name a visitor writes under is kept too
// ("weesite:commenter"). A signed-in editor comments under their own name. Each comment gets Reply,
// which quotes it into the form; the writer's own get Edit and Delete; the post's moderators see the
// comments waiting, with Approve and Reject, and admins can reject a published one. Dates read as
// "2 days ago", the exact time on hover or a click; Show more opens ten more at a time. Texts come
// from the element's data attributes, written from the site's resources.

import { AnswerSupply } from "./visitor-proof.js";

const STORE = "weesite:comments";
const NAME = "weesite:commenter";

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

const remembered = () => {
  try {
    return localStorage.getItem(NAME) ?? "";
  } catch {
    return "";
  }
};

const remember = (name) => {
  try {
    localStorage.setItem(NAME, name);
  } catch {
    // Not kept: the visitor types it next time.
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

const button = (text, className = "btn small") => {
  const node = element("button", className, text);
  node.type = "button";
  return node;
};

const fill = (template, value) => (template ?? "").replace("{0}", value ?? "");

// "5 minutes ago", "2 days ago"; null from a year on, when the date itself reads better.
const relative = (date, lang) => {
  const seconds = (date.getTime() - Date.now()) / 1000;
  const away = Math.abs(seconds);
  const steps = [
    [60, 1, "second"],
    [3600, 60, "minute"],
    [86400, 3600, "hour"],
    [604800, 86400, "day"],
    [2629800, 604800, "week"],
    [31557600, 2629800, "month"],
  ];
  const step = steps.find(([below]) => away < below);
  if (!step) {
    return null;
  }
  try {
    const format = new Intl.RelativeTimeFormat(lang || undefined, { numeric: "auto" });
    return format.format(Math.round(seconds / step[1]), step[2]);
  } catch {
    return null;
  }
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
    this.section = this.closest("section") ?? document;
    this.form = this.querySelector("form[data-comment-form]");
    this.own = this.querySelector("[data-own]");
    this.status = this.querySelector("[data-status]");
    this.error = this.querySelector("[data-error]");
    this.supply = new AnswerSupply("comment");
    if (!this.form || !this.own) {
      return;
    }

    this.dates();
    this.showMore();
    const [open, viewer] = await Promise.all([this.renewToken(), this.whoIsHere()]);
    this.viewer = viewer;
    this.form.hidden = !open;
    this.hidden = false;
    this.nameField(viewer);
    if (open) {
      this.replies();
    }
    // The comment count beside React (T-0162): to the comments, and into the box when it's there.
    for (const jump of document.querySelectorAll("a[data-comment-jump]")) {
      jump.addEventListener("click", (event) => {
        const text = this.form.elements.namedItem("text");
        if (this.form.hidden || !(text instanceof HTMLTextAreaElement)) {
          return;
        }
        event.preventDefault();
        text.scrollIntoView({ block: "center" });
        text.focus({ preventScroll: true });
      });
    }
    this.form.addEventListener("focusin", () => this.supply.prepare(), { once: true });
    this.form.addEventListener("submit", (event) => {
      event.preventDefault();
      this.send();
    });
    await this.showOwn();
    await this.moderation();
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

  // A signed-in editor, and whether they moderate this post; nobody, for a visitor.
  async whoIsHere() {
    try {
      const response = await fetch(`/visitor/comments/me?post=${encodeURIComponent(this.post)}`, {
        headers: { Accept: "application/json" },
        cache: "no-store",
        credentials: "same-origin",
      });
      return response.ok ? await response.json() : {};
    } catch {
      return {};
    }
  }

  // An editor's name is theirs, said beside the field; a visitor's comes back from last time.
  nameField(viewer) {
    const name = this.form.elements.namedItem("name");
    if (!(name instanceof HTMLInputElement)) {
      return;
    }
    if (viewer?.editor && viewer.name) {
      name.value = viewer.name;
      name.readOnly = true;
      const note = element("p", "help", fill(this.dataset.commentingAs, viewer.name));
      note.id = "comment-name-note";
      name.setAttribute("aria-describedby", note.id);
      name.after(note);
    } else if (!name.value) {
      name.value = remembered();
    }
  }

  // Dates as "2 days ago"; the exact time in the tooltip, and in place after a click.
  dates() {
    const lang = this.dataset.lang || document.documentElement.lang;
    for (const time of this.section.querySelectorAll("time.comment-time")) {
      this.relativeTime(time, lang);
    }
  }

  relativeTime(time, lang) {
    const when = new Date(time.dateTime);
    const near = Number.isNaN(when.getTime()) ? null : relative(when, lang);
    if (!near || time.closest("button")) {
      return;
    }
    const exact = time.title || time.textContent;
    const toggle = button("", "time-toggle");
    toggle.title = exact;
    toggle.setAttribute("aria-pressed", "false");
    time.replaceWith(toggle);
    toggle.append(time);
    time.textContent = near;
    toggle.addEventListener("click", () => {
      const showing = toggle.getAttribute("aria-pressed") === "true";
      time.textContent = showing ? near : exact;
      toggle.setAttribute("aria-pressed", String(!showing));
    });
  }

  // Show more: the next ten, the focus on the first of them.
  showMore() {
    const link = this.section.querySelector("a[data-show-more]");
    if (!link) {
      return;
    }
    const step = Number(link.dataset.step) || 10;
    link.addEventListener("click", (event) => {
      event.preventDefault();
      const hidden = [...this.section.querySelectorAll("[data-comment-list] > li[hidden]")];
      const next = hidden.slice(0, step);
      for (const item of next) {
        item.hidden = false;
      }
      const left = hidden.length - next.length;
      if (left > 0) {
        link.textContent = fill(link.dataset.more, String(left));
      } else {
        link.closest("p")?.remove();
      }
      const first = next[0]?.querySelector("article");
      if (first) {
        first.tabIndex = -1;
        first.focus();
      }
    });
  }

  // Reply quotes a comment into the form, as in mail: each line after "> ".
  replies() {
    const text = this.form.elements.namedItem("text");
    for (const article of this.section.querySelectorAll("[data-comment-list] article[data-comment-id]")) {
      const actions = article.querySelector("[data-actions]");
      if (!actions || actions.querySelector(".reply")) {
        continue;
      }
      const name = article.querySelector(".comment-name")?.textContent ?? "";
      const reply = button(this.dataset.reply, "btn small reply");
      reply.setAttribute("aria-label", fill(this.dataset.replyTo, name));
      reply.addEventListener("click", () => {
        if (!(text instanceof HTMLTextAreaElement)) {
          return;
        }
        const quote = (article.dataset.text ?? "")
          .split("\n")
          .map((line) => `> ${line}`)
          .join("\n");
        const before = text.value.trim();
        text.value = `${before ? `${before}\n\n` : ""}${quote}\n\n`;
        text.focus();
        text.setSelectionRange(text.value.length, text.value.length);
        text.scrollIntoView({ block: "center" });
      });
      actions.prepend(reply);
    }
  }

  async send() {
    if (this.sending) {
      return;
    }
    this.sending = true;
    const submit = this.form.querySelector("button[type=submit]");
    submit?.setAttribute("aria-disabled", "true");
    this.showError("");
    this.say(this.dataset.sending);
    const name = this.form.elements.namedItem("name");
    const text = this.form.elements.namedItem("text");
    const website = this.form.elements.namedItem("website");
    try {
      let proof = null;
      if (!this.viewer?.editor) {
        try {
          proof = await this.supply.take();
        } catch {
          // No answer: the server says what to do.
        }
      }
      const response = await fetch(this.url(), {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        credentials: "same-origin",
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
      if (!this.viewer?.editor && name?.value) {
        remember(name.value);
      }
      if (text) {
        text.value = "";
      }

      // Published at once (an admin's, or the post's author's): the page shows it in its place.
      if (answer.status === "approved") {
        this.say(this.dataset.sentPublic);
        location.hash = `comment-${answer.id}`;
        location.reload();
        return;
      }
      await this.renewToken();
      this.say(this.dataset.sent);
      await this.showOwn();
    } catch {
      this.say("");
      this.showError(this.dataset.failed);
    } finally {
      this.sending = false;
      submit?.removeAttribute("aria-disabled");
    }
  }

  mark(by) {
    return by === "author" ? this.dataset.markAuthor : by === "editor" ? this.dataset.markEditor : null;
  }

  // A comment that isn't in the page's list: this browser's waiting one, or one a moderator sees.
  article(comment, className, note) {
    const article = element("article", className);
    const head = element("div", "comment-head");
    const meta = element("p", "comment-meta");
    meta.append(element("span", "comment-name", comment.name));
    const mark = this.mark(comment.by);
    if (mark) {
      meta.append(element("span", "comment-mark", mark));
    }
    const time = element("time", "comment-time", new Date(comment.created).toLocaleString(this.dataset.lang || undefined));
    time.dateTime = comment.created;
    time.title = time.textContent;
    meta.append(time);
    const actions = element("div", "comment-actions");
    head.append(meta, actions);
    article.append(head, element("p", "comment-text", comment.pendingText ?? comment.text));
    if (note) {
      article.append(element("p", "own-note", note));
    }
    this.relativeTime(time, this.dataset.lang || document.documentElement.lang);
    return { article, actions };
  }

  // This browser's comments on this post: waiting ones in their own list, published ones marked.
  async showOwn() {
    this.own.replaceChildren();
    for (const old of this.section.querySelectorAll(".own-note, .own-label, .own-controls")) {
      old.remove();
    }
    for (const marked of this.section.querySelectorAll("article.own")) {
      marked.classList.remove("own");
    }
    this.mine = new Set();
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
      this.mine.add(id);

      const published = comment.status === "approved" ? document.getElementById(`comment-${id}`) : null;
      if (published) {
        published.classList.add("own");
        published.querySelector(".comment-meta")?.append(element("span", "own-label", this.dataset.yours));
        if (comment.pendingText) {
          published.append(element("p", "own-note", this.dataset.editWaiting));
        }
        published.querySelector("[data-actions]")?.append(this.controls(comment, entry.token));
        continue;
      }

      const note = comment.status === "rejected"
        ? this.dataset.rejected
        : comment.status === "approved" && comment.pendingText ? this.dataset.editWaiting : this.dataset.waiting;
      const { article, actions } = this.article(comment, `comment own ${comment.status === "rejected" ? "" : "waiting"}`.trim(), note);
      article.id = `own-${id}`;
      article.querySelector(".comment-meta")?.append(element("span", "own-label", this.dataset.yours));
      actions.append(this.controls(comment, entry.token));
      this.own.append(article);
    }
  }

  controls(comment, token) {
    const box = element("div", "own-controls");
    const edit = button(this.dataset.edit);
    const remove = button(this.dataset.delete, "btn small danger");
    if (comment.status !== "rejected") {
      box.append(edit);
    }
    box.append(remove);

    edit.addEventListener("click", () => this.edit(comment, token, box));
    remove.addEventListener("click", () => {
      // Two steps instead of a dialog: Delete, then Delete for good or Cancel.
      const really = button(this.dataset.deleteConfirm, "btn small danger");
      const cancel = button(this.dataset.cancel);
      box.replaceChildren(really, cancel);
      really.focus();
      cancel.addEventListener("click", () => this.showOwn());
      really.addEventListener("click", () => this.remove(comment, token));
    });
    return box;
  }

  edit(comment, token, box) {
    const article = box.closest("article");
    const form = element("form", "own-edit");
    const label = element("label", null, this.dataset.editLabel);
    const area = element("textarea");
    area.id = `edit-${comment.id}`;
    label.htmlFor = area.id;
    area.required = true;
    area.rows = 5;
    area.maxLength = Number(this.dataset.textMax) || 1000;
    area.value = comment.pendingText ?? comment.text;
    const save = element("button", "btn small primary", this.dataset.save);
    save.type = "submit";
    const cancel = button(this.dataset.cancel);
    const actions = element("div", "own-controls");
    actions.append(save, cancel);
    form.append(label, area, actions);
    (article ?? box).append(form);
    box.hidden = true;
    area.focus();
    if (!this.viewer?.moderator) {
      this.supply.prepare();
    }

    cancel.addEventListener("click", () => {
      form.remove();
      box.hidden = false;
      box.querySelector("button")?.focus();
    });
    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      this.say(this.dataset.sending);
      try {
        let proof = null;
        if (!this.viewer?.moderator) {
          try {
            proof = await this.supply.take();
          } catch {
            // The server says what to do.
          }
        }
        const response = await fetch(this.url(comment.id), {
          method: "PUT",
          headers: { "Content-Type": "application/json", Accept: "application/json", Authorization: `Comment ${token}` },
          credentials: "same-origin",
          body: JSON.stringify({ text: area.value, proof }),
        });
        const answer = await response.json().catch(() => ({}));
        if (!response.ok) {
          this.say(answer.message ?? this.dataset.failed);
          area.setAttribute("aria-invalid", "true");
          area.focus();
          return;
        }
        if (this.viewer?.moderator && answer.status === "approved") {
          location.reload();
          return;
        }
        this.say(this.dataset.saved);
        form.remove();
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
    this.form?.querySelector("textarea")?.focus();
  }

  // The post's moderators: the comments waiting, with Approve and Reject; admins can also reject
  // a published one. Approving shows the page again, with the comment in its place.
  async moderation() {
    if (!this.viewer?.moderator) {
      return;
    }
    if (this.viewer.admin) {
      for (const article of this.section.querySelectorAll("[data-comment-list] article[data-comment-id]")) {
        const reject = button(this.dataset.reject, "btn small danger");
        reject.addEventListener("click", () => this.decide(article.dataset.commentId, "reject", article.closest("li")));
        article.querySelector("[data-actions]")?.append(reject);
      }
    }

    let waiting = [];
    try {
      const response = await fetch(`/visitor/comments/moderation?post=${encodeURIComponent(this.post)}`, {
        headers: { Accept: "application/json" },
        cache: "no-store",
        credentials: "same-origin",
      });
      waiting = response.ok ? await response.json() : [];
    } catch {
      return;
    }
    for (const comment of waiting) {
      if (this.mine?.has(comment.id)) {
        continue;
      }
      const edit = comment.status === "approved" && comment.pendingText;
      const { article, actions } = this.article(comment, "comment waiting", this.dataset.waitingMod);
      if (edit) {
        article.querySelector(".comment-text")?.before(element("p", "own-note", this.dataset.editOf));
      }
      const approve = button(this.dataset.approve, "btn small primary");
      const reject = button(this.dataset.reject, "btn small");
      approve.addEventListener("click", () => this.decide(comment.id, "approve", article));
      reject.addEventListener("click", () => this.decide(comment.id, "reject", article));
      actions.append(approve, reject);
      this.own.append(article);
    }
  }

  async decide(id, decision, shown) {
    try {
      const response = await fetch("/visitor/comments/moderation", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        credentials: "same-origin",
        body: JSON.stringify({ post: this.post, id, decision }),
      });
      if (!response.ok) {
        const answer = await response.json().catch(() => ({}));
        this.say(answer.message ?? this.dataset.failed);
        return;
      }
    } catch {
      this.say(this.dataset.failed);
      return;
    }
    if (decision === "approve") {
      location.hash = `comment-${id}`;
      location.reload();
      return;
    }
    shown?.remove();
    this.say(this.dataset.rejectedMod);
    this.form?.querySelector("textarea")?.focus();
  }
}

if (!customElements.get("ws-comments")) {
  customElements.define("ws-comments", WsComments);
}
