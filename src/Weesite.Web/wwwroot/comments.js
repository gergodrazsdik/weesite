// <ws-comments>: the comment form, the writer's own comments and what a post's comments do with
// JavaScript (ADR 0011, T-0161). The server renders the form hidden; this shows it once it knows the
// post takes comments, with a fresh form token, and sends a comment as JSON with a proof of work,
// solved in the background from the first focus. A new comment's token comes back once: it's kept in
// localStorage ("weesite:comments": comment ID to the post's key and the token), and sent in a header
// to read, edit or delete that comment. The name a visitor writes under is kept too
// ("weesite:commenter"). A signed-in editor comments under their own name. Each comment gets Reply,
// which quotes it into the form; the writer's own get Edit and Delete; the post's moderators see the
// comments waiting, with Approve and Reject, and admins can reject a published one. Dates read as
// "2 days ago", the exact time on hover or a click, in the site's language (<html lang>), as the
// rest of the page around a post in another language; Show more opens ten more at a time. Texts come
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

const fill = (template, ...values) => values.reduce((text, value, index) => text.replace(`{${index}}`, value ?? ""), template ?? "");

// Icons for the comments' buttons (T-0165): each button keeps its name for screen readers and a tooltip.
const ICONS = {
  reply: ["M9 14 4 9l5-5", "M4 9h10.5a5.5 5.5 0 0 1 0 11H11"],
  edit: ["M12 20h9", "M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"],
  delete: ["M3 6h18", "M8 6V4h8v2", "M19 6l-1 14H6L5 6"],
  approve: ["M20 6 9 17l-5-5"],
  reject: ["M18 6 6 18", "M6 6l12 12"],
};

const iconButton = (label, icon, className = "btn small icon") => {
  const node = button("", className);
  node.setAttribute("aria-label", label ?? "");
  node.title = label ?? "";
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  for (const [name, value] of Object.entries({ width: "16", height: "16", viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", "stroke-width": "2", "stroke-linecap": "round", "stroke-linejoin": "round", "aria-hidden": "true", focusable: "false" })) {
    svg.setAttribute(name, value);
  }
  for (const d of ICONS[icon] ?? []) {
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", d);
    svg.append(path);
  }
  node.append(svg);
  return node;
};

// Quotes nest this deep; a deeper > or | stays in the text (as CommentSection.razor's QuoteDepth).
const QUOTE_DEPTH = 4;

// Runs of quoted lines (without their mark) and paragraphs between empty lines, into box; a quote's
// own lines are read the same way, so a quote of a quote nests.
const blocks = (box, text, depth) => {
  let lines = null;
  let quoting = false;
  const flush = () => {
    if (lines && lines.some((line) => line.length > 0)) {
      if (quoting) {
        const quote = element("blockquote");
        blocks(quote, lines, depth + 1);
        box.append(quote);
      } else {
        box.append(element("p", null, lines.join("\n")));
      }
    }
    lines = null;
  };
  for (const line of text) {
    const quote = depth <= QUOTE_DEPTH && (line.startsWith(">") || line.startsWith("|"));
    if (lines && (quote !== quoting || (!quote && line.length === 0))) {
      flush();
    }
    if (!quote && line.length === 0) {
      continue;
    }
    lines ??= [];
    quoting = quote;
    lines.push(quote ? line.slice(1).replace(/^ +/, "") : line);
  }
  flush();
};

// A comment's text as the page shows it: paragraphs, and lines starting with > or | as quotes, as
// the server renders the published ones (CommentSection.razor).
const quoted = (text) => {
  const box = element("div", "comment-text");
  blocks(box, (text ?? "").split("\n"), 1);
  return box;
};

// The site's language: dates and numbers follow it, not the post's (docs/languages.md).
const siteLang = () => document.documentElement.lang || undefined;

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
    // "No comments yet" gives way while this browser's own or the waiting ones show.
    const none = this.section.querySelector(".comments-none");
    if (none) {
      const update = () => {
        none.hidden = this.own.children.length > 0;
      };
      new MutationObserver(update).observe(this.own, { childList: true });
      update();
    }
    const [open, viewer] = await Promise.all([this.renewToken(), this.whoIsHere()]);
    this.viewer = viewer;
    this.form.hidden = !open;
    this.hidden = false;
    this.nameField(viewer);
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
    if (open) {
      this.replies();
    }
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
      // Signed in, the name is the editor's own and can't change (T-0165): the field gives way to
      // a line saying whose name it is.
      name.value = viewer.name;
      name.readOnly = true;
      const field = name.closest(".field");
      const note = element("p", "help commenting-as", fill(this.dataset.commentingAs, viewer.name));
      field?.before(note);
      if (field) {
        field.hidden = true;
      }
    } else if (!name.value) {
      name.value = remembered();
    }
  }

  // Dates as "2 days ago"; the exact time in the tooltip, and in place after a click.
  dates() {
    const lang = siteLang();
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
    const lang = siteLang();
    for (const article of this.section.querySelectorAll("[data-comment-list] article[data-comment-id]:not(.deleted)")) {
      const actions = article.querySelector("[data-actions]");
      if (!actions || actions.querySelector(".reply") || this.mine?.has(article.dataset.commentId)) {
        continue;
      }
      const name = article.querySelector(".comment-name")?.textContent ?? "";
      const reply = iconButton(fill(this.dataset.replyTo, name), "reply", "btn small icon reply");
      reply.addEventListener("click", () => {
        if (!(text instanceof HTMLTextAreaElement)) {
          return;
        }
        // Who wrote it and when, then the text quoted, as mail does (T-0165).
        const when = new Date(article.querySelector("time")?.dateTime ?? "");
        const day = Number.isNaN(when.getTime()) ? "" : when.toLocaleDateString(lang, { year: "numeric", month: "long", day: "numeric" });
        const quote = `${fill(this.dataset.quoteHeader, name, day)}\n${(article.dataset.text ?? "")
          .split("\n")
          .map((line) => `> ${line}`)
          .join("\n")}`;
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
    const time = element("time", "comment-time", new Date(comment.created).toLocaleString(siteLang()));
    time.dateTime = comment.created;
    time.title = time.textContent;
    meta.append(time);
    const actions = element("div", "comment-actions");
    head.append(meta, actions);
    article.append(head, quoted(comment.pendingText ?? comment.text));
    if (note) {
      article.append(element("p", "own-note", note));
    }
    this.relativeTime(time, siteLang());
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
    const edit = iconButton(this.dataset.edit, "edit");
    const remove = iconButton(this.dataset.delete, "delete", "btn small icon danger");
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
    this.leave(document.getElementById(`comment-${comment.id}`));
    this.say(this.dataset.deleted);
    await this.showOwn();
    this.form?.querySelector("textarea")?.focus();
  }

  // A published comment its writer deleted: where a later comment follows, a marker keeps its place,
  // so the replies around it read, as the server shows it (F-0203); at the end it goes, and so do
  // the markers it leaves last.
  leave(article) {
    const item = article?.closest("li");
    if (!article || !item) {
      return;
    }
    let next = item.nextElementSibling;
    while (next && !next.querySelector("article:not(.deleted)")) {
      next = next.nextElementSibling;
    }
    if (next) {
      const meta = element("p", "comment-meta");
      meta.append(element("span", "comment-deleted", this.dataset.deletedByWriter));
      const time = article.querySelector("time.comment-time");
      if (time) {
        meta.append(time.closest(".time-toggle") ?? time);
      }
      article.className = "comment deleted";
      delete article.dataset.text;
      article.replaceChildren(meta);
      return;
    }
    let previous = item.previousElementSibling;
    item.remove();
    while (previous?.querySelector("article.deleted")) {
      const before = previous.previousElementSibling;
      previous.remove();
      previous = before;
    }
  }

  // The post's moderators: the comments waiting, with Approve and Reject; admins can also reject
  // a published one. Approving shows the page again, with the comment in its place.
  async moderation() {
    if (!this.viewer?.moderator) {
      return;
    }
    if (this.viewer.admin) {
      for (const article of this.section.querySelectorAll("[data-comment-list] article[data-comment-id]:not(.deleted)")) {
        if (this.mine?.has(article.dataset.commentId)) {
          continue;
        }
        const reject = iconButton(this.dataset.reject, "reject", "btn small icon danger");
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
      // An edit shows in the comment it changes, under the published text, as the admin's
      // Comments page shows it beside it (F-0200); shown as well when it's behind Show more.
      const published = edit ? this.section.querySelector(`[data-comment-list] article[data-comment-id="${CSS.escape(comment.id)}"]`) : null;
      if (published) {
        const box = element("div", "comment-edit");
        box.append(element("p", "own-note", this.dataset.editOf), quoted(comment.pendingText));
        const controls = element("div", "own-controls");
        const approve = button(this.dataset.approveEdit, "btn small primary");
        const reject = button(this.dataset.rejectEdit);
        approve.addEventListener("click", () => this.decide(comment.id, "approve", box));
        reject.addEventListener("click", () => this.decide(comment.id, "reject", box, this.dataset.editRejectedMod));
        controls.append(approve, reject);
        box.append(controls);
        published.append(box);
        const item = published.closest("li");
        if (item) {
          item.hidden = false;
        }
        continue;
      }
      const { article, actions } = this.article(comment, "comment waiting", this.dataset.waitingMod);
      if (edit) {
        article.querySelector(".comment-text")?.before(element("p", "own-note", this.dataset.editOf));
      }
      const approve = iconButton(this.dataset.approve, "approve", "btn small icon primary");
      const reject = iconButton(this.dataset.reject, "reject");
      approve.addEventListener("click", () => this.decide(comment.id, "approve", article));
      reject.addEventListener("click", () => this.decide(comment.id, "reject", article));
      actions.append(approve, reject);
      this.own.append(article);
    }
  }

  async decide(id, decision, shown, rejected = this.dataset.rejectedMod) {
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
    this.say(rejected);
    this.form?.querySelector("textarea")?.focus();
  }
}

if (!customElements.get("ws-comments")) {
  customElements.define("ws-comments", WsComments);
}
