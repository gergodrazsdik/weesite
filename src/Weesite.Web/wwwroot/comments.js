// <ws-comments>: the comment form, the writer's own comments and what a post's comments do with
// JavaScript (ADR 0011, T-0161). The server renders the form hidden; this shows it once it knows the
// post takes comments, with a fresh form token, and sends a comment as JSON with a proof of work,
// solved in the background from the first focus. A new comment's token comes back once: it's kept in
// localStorage ("weesite:comments": comment ID to the post's key and the token), and sent in a header
// to read, edit or delete that comment. The name a visitor writes under is kept too
// ("weesite:commenter"). A signed-in editor comments under their own name. Each comment gets Reply,
// which quotes it into the form; the writer's own get Edit and Delete beside it; the post's moderators see the
// comments waiting, with Approve and Reject, and admins can remove a published one. Dates read as
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

// The addresses in a published comment's text as links, as the server finds them (VisitorText.Links):
// http(s):// or www., without the punctuation after them, a closing bracket only with its opening
// one; marked rel="ugc nofollow noopener", as CommentSection.razor marks them.
const LINK = /(?:\bhttps?:\/\/|\bwww\.)[^\s<>"]+/gi;

const linked = (paragraph, text) => {
  let at = 0;
  for (const match of text.matchAll(LINK)) {
    let value = match[0].replace(/[.,;:!?'"*_]+$/, "");
    const count = (c) => [...value].filter((x) => x === c).length;
    while (value.length > 0 && (value.endsWith(")") || value.endsWith("]")) && count(value.endsWith(")") ? "(" : "[") < count(value.at(-1))) {
      value = value.slice(0, -1).replace(/[.,;:!?]+$/, "");
    }
    if (value.length <= 4) {
      continue;
    }
    let url;
    try {
      url = new URL(/^www\./i.test(value) ? `https://${value}` : value);
    } catch {
      continue;
    }
    if (url.protocol !== "http:" && url.protocol !== "https:") {
      continue;
    }
    paragraph.append(text.slice(at, match.index));
    const link = element("a", null, value);
    link.href = url.href;
    link.rel = "ugc nofollow noopener";
    paragraph.append(link);
    at = match.index + value.length;
  }
  paragraph.append(text.slice(at));
  return paragraph;
};

// Runs of quoted lines (without their mark) and paragraphs between empty lines, into box; a quote's
// own lines are read the same way, so a quote of a quote nests. A published comment's addresses are
// links; a waiting one's stay text, as the moderators' list shows them.
const blocks = (box, text, depth, links) => {
  let lines = null;
  let quoting = false;
  const flush = () => {
    if (lines && lines.some((line) => line.length > 0)) {
      if (quoting) {
        const quote = element("blockquote");
        blocks(quote, lines, depth + 1, links);
        box.append(quote);
      } else {
        box.append(links ? linked(element("p"), lines.join("\n")) : element("p", null, lines.join("\n")));
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
const quoted = (text, links = false) => {
  const box = element("div", "comment-text");
  blocks(box, (text ?? "").split("\n"), 1, links);
  return box;
};

// The site's language: dates and numbers follow it, not the post's (docs/languages.md).
const siteLang = () => document.documentElement.lang || undefined;

// The most a quote may take of the comment box: a third of its limit, so a reply fits (F-0250).
const quoteMax = (box) => Math.floor((box.maxLength > 0 ? box.maxLength : 1000) / 3);

// A comment's text cut to at most max characters, between words, with "…" where it was cut: an
// address that would be cut in two goes whole.
const excerpt = (text, max) => {
  if (text.length <= max) {
    return text;
  }
  const cut = text.slice(0, max);
  const space = cut.search(/\s\S*$/);
  return `${(space > 0 ? cut.slice(0, space) : cut).trimEnd()} …`;
};

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
    // What waits for a moderator lives apart from this browser's own, which redraw (F-0211).
    this.waitingBox = this.querySelector("[data-moderation]") ?? this.own;
    this.status = this.querySelector("[data-status]");
    this.error = this.querySelector("[data-error]");
    this.supply = new AnswerSupply("comment");
    if (!this.form || !this.own) {
      return;
    }

    this.dates();
    this.showMore();
    this.showLinked();
    // "No comments yet" gives way while this browser's own or the waiting ones show.
    const none = this.section.querySelector(".comments-none");
    if (none) {
      const update = () => {
        none.hidden = this.own.children.length > 0 || this.waitingBox.children.length > 0;
      };
      const observer = new MutationObserver(update);
      observer.observe(this.own, { childList: true });
      observer.observe(this.waitingBox, { childList: true });
      update();
    }
    const [open, viewer] = await Promise.all([this.renewToken(), this.whoIsHere()]);
    this.viewer = viewer;
    this.open = open;
    this.form.hidden = !open;
    this.hidden = false;
    this.nameField(viewer);
    // A moderator's comments are public at once: the help doesn't say they wait (U-20).
    if (viewer?.moderator) {
      this.form.querySelector("[data-approval-note]")?.remove();
    }
    // The comment count beside React (T-0162): to the comments, and into the box when it's there.
    for (const jump of document.querySelectorAll("a[data-comment-jump]")) {
      // Its name offers to write one only once there's a form to write in (F-0246).
      if (open && jump.dataset.writeLabel) {
        jump.setAttribute("aria-label", jump.dataset.writeLabel);
      }
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

  // What's wrong with the form, as the contact form says it (U-15): above the form, each with a
  // link to its field, and beside the field, which points at it with aria-describedby and is
  // marked invalid; the focus goes to the first such field, or to the message when none is named.
  // problems: [{ field, text }]; general: a message about no one field. Nothing clears them.
  showErrors(problems = [], general = "") {
    for (const input of this.form.querySelectorAll("input, textarea")) {
      input.removeAttribute("aria-invalid");
      const note = input.id ? this.form.querySelector(`#${CSS.escape(input.id)}-error`) : null;
      if (note) {
        note.textContent = "";
        note.hidden = true;
        const rest = (input.getAttribute("aria-describedby") ?? "").split(/\s+/).filter((id) => id && id !== note.id);
        if (rest.length > 0) {
          input.setAttribute("aria-describedby", rest.join(" "));
        } else {
          input.removeAttribute("aria-describedby");
        }
      }
    }
    if (!this.error) {
      return;
    }
    this.error.replaceChildren();
    const shown = problems.filter(({ field }) => field instanceof HTMLElement);
    for (const { field, text } of shown) {
      const note = this.form.querySelector(`#${CSS.escape(field.id)}-error`);
      if (note) {
        note.textContent = text ?? "";
        note.hidden = false;
        field.setAttribute("aria-describedby", [note.id, ...(field.getAttribute("aria-describedby") ?? "").split(/\s+/).filter(Boolean)].join(" "));
      }
      field.setAttribute("aria-invalid", "true");
      const line = element("p", null, `${text ?? ""} `);
      const go = element("a", null, this.dataset.goToField);
      go.href = `#${field.id}`;
      go.addEventListener("click", (event) => {
        event.preventDefault();
        field.focus();
      });
      line.append(go);
      this.error.append(line);
    }
    if (general) {
      this.error.append(element("p", null, general));
    }
    this.error.hidden = this.error.childElementCount === 0;
    if (shown.length > 0) {
      shown[0].field.focus();
    } else if (!this.error.hidden) {
      this.error.tabIndex = -1;
      this.error.focus();
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
    // The tooltip is the button's: on the time inside it, it became the button's name, which then
    // didn't start with the words it shows (U-19).
    toggle.title = exact;
    time.removeAttribute("title");
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

  // Show more opens the next ten comments, with the markers of deleted ones before them, the focus
  // on the first; the markers don't count (F-0206). A marker opens only with the comment after it,
  // as the server places them (CommentSection's ranks, F-0212).
  showMore() {
    const link = this.section.querySelector("a[data-show-more]");
    if (!link) {
      return;
    }
    const step = Number(link.dataset.step) || 10;
    const isComment = (item) => !item.querySelector("article.deleted");
    link.addEventListener("click", (event) => {
      event.preventDefault();
      const hidden = [...this.section.querySelectorAll("[data-comment-list] > li[hidden]")];
      const next = [];
      let markers = [];
      let opened = 0;
      for (const item of hidden) {
        if (!isComment(item)) {
          markers.push(item);
          continue;
        }
        if (opened === step) {
          break;
        }
        opened++;
        next.push(...markers, item);
        markers = [];
      }
      for (const item of next) {
        item.hidden = false;
      }
      const left = hidden.slice(next.length).filter(isComment).length;
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

  // A link to a comment behind Show more, as the page has after a comment is published or approved
  // here: every comment shows, and the page goes to that one (F-0206).
  showLinked() {
    let id = "";
    try {
      id = decodeURIComponent(location.hash.slice(1));
    } catch {
      // A malformed address (a stray %): no comment to go to, and the rest still starts (F-0213).
      return;
    }
    const target = id.startsWith("comment-") ? document.getElementById(id) : null;
    if (!target?.closest("li")?.hidden) {
      return;
    }
    for (const item of this.section.querySelectorAll("[data-comment-list] > li[hidden]")) {
      item.hidden = false;
    }
    this.section.querySelector("a[data-show-more]")?.closest("p")?.remove();
    target.scrollIntoView({ block: "start" });
  }

  // A comment that's just been published, here or by a moderator here, into the list as the server
  // renders it (CommentSection.razor), in its place by ID (oldest first), so the page needn't be
  // reloaded (U-11). Its addresses are links, as there.
  publish(comment) {
    let list = this.section.querySelector("[data-comment-list]");
    if (!list) {
      list = element("ol", "comment-list");
      list.setAttribute("role", "list");
      list.dataset.commentList = "";
      const none = this.section.querySelector(".comments-none");
      (none ?? this).before(list);
      none?.remove();
    }
    const article = element("article", comment.by === "author" ? "comment by-author" : comment.by === "editor" ? "comment by-editor" : "comment");
    article.id = `comment-${comment.id}`;
    article.dataset.commentId = comment.id;
    article.dataset.text = comment.text;
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
    actions.dataset.actions = "";
    head.append(meta, actions);
    article.append(head, quoted(comment.text, true));
    const item = element("li");
    item.append(article);
    const after = [...list.querySelectorAll(":scope > li > article[data-comment-id]")].find((other) => other.dataset.commentId > comment.id);
    list.insertBefore(item, after?.closest("li") ?? null);
    this.relativeTime(time, siteLang());
    this.counted();
    return article;
  }

  // A published comment's new text, in place (U-12): marked edited, as the server marks it.
  republish(article, text) {
    article.dataset.text = text;
    article.querySelector(":scope > .comment-text")?.replaceWith(quoted(text, true));
    const meta = article.querySelector(".comment-meta");
    if (meta && !meta.querySelector(".comment-mark.edited")) {
      const edited = element("span", "comment-mark edited", this.dataset.edited);
      edited.title = fill(this.dataset.editedAt, new Date().toLocaleString(siteLang()));
      meta.insertBefore(edited, meta.querySelector(".own-label, .time-toggle, time.comment-time"));
    }
  }

  // Shows a comment the page just changed: Show more opens up to it, and the page scrolls to it with
  // the focus on it (U-11); the status line says what happened.
  goTo(article) {
    if (!article) {
      this.form?.querySelector("textarea")?.focus();
      return;
    }
    const item = article.closest("[data-comment-list] > li");
    if (item) {
      const items = [...this.section.querySelectorAll("[data-comment-list] > li")];
      for (const before of items.slice(0, items.indexOf(item) + 1)) {
        before.hidden = false;
      }
    }
    const link = this.section.querySelector("a[data-show-more]");
    if (link) {
      const left = [...this.section.querySelectorAll("[data-comment-list] > li[hidden]")].filter((li) => !li.querySelector("article.deleted")).length;
      if (left > 0) {
        link.textContent = fill(link.dataset.more, String(left));
      } else {
        link.closest("p")?.remove();
      }
    }
    article.tabIndex = -1;
    article.scrollIntoView({ block: "center" });
    article.focus({ preventScroll: true });
  }

  // The comment count beside React, from the list as the page shows it now (U-14).
  counted() {
    const count = this.section.querySelectorAll("[data-comment-list] article[data-comment-id]:not(.deleted)").length;
    for (const jump of document.querySelectorAll("a[data-comment-jump]")) {
      const number = jump.querySelector("span");
      if (number) {
        number.textContent = count.toLocaleString(siteLang());
      }
      const words = this.open && jump.dataset.writeLabel ? "write" : "read";
      const template = jump.dataset[`${words}${count === 0 ? "None" : count === 1 ? "One" : "Many"}`];
      if (template) {
        jump.setAttribute("aria-label", fill(template, count.toLocaleString(siteLang())));
      }
    }
  }


  // Reply quotes a comment into the form, as in mail: each line after "> ". A long one is quoted
  // only in part, cut between words, so the quote leaves room for the reply within the limit
  // (F-0250); the server doesn't count the links in quoted lines that the post's comments have.
  // Every published comment gets Reply, one's own too (U-22), first among its buttons. The header
  // is in one language, its words and its date (U-16): the server chose it (CommentQuote).
  replies() {
    const text = this.form.elements.namedItem("text");
    const lang = this.dataset.quoteLang || siteLang();
    for (const article of this.section.querySelectorAll("[data-comment-list] article[data-comment-id]:not(.deleted)")) {
      const actions = article.querySelector("[data-actions]");
      if (!actions || actions.querySelector(".reply")) {
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
        const quote = `${fill(this.dataset.quoteHeader, name, day)}\n${excerpt(article.dataset.text ?? "", quoteMax(text))
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
    const name = this.form.elements.namedItem("name");
    const text = this.form.elements.namedItem("text");
    const website = this.form.elements.namedItem("website");
    // An empty field is said here, in the site's words, rather than in the browser's (U-04).
    const missing = [];
    if (!this.viewer?.editor && name instanceof HTMLInputElement && !name.value.trim()) {
      missing.push({ field: name, text: this.dataset.nameMissing });
    }
    if (text instanceof HTMLTextAreaElement && !text.value.trim()) {
      missing.push({ field: text, text: this.dataset.textMissing });
    }
    if (missing.length > 0) {
      this.say("");
      this.showErrors(missing);
      return;
    }
    this.sending = true;
    const submit = this.form.querySelector("button[type=submit]");
    submit?.setAttribute("aria-disabled", "true");
    this.showErrors();
    this.say(this.dataset.sending);
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
        const field = answer.error === "bad-name" && !this.viewer?.editor ? name : answer.error === "bad-text" ? text : null;
        const message = answer.message ?? this.dataset.failed;
        if (field) {
          this.showErrors([{ field, text: message }]);
        } else {
          this.showErrors([], message);
        }
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

      // Published at once (an admin's, or the post's author's): it goes into the list from the
      // server's answer, not from a reload, which another replica may answer without it for a
      // while (U-11). Waiting, it shows among this browser's own.
      const published = answer.status === "approved";
      if (published) {
        this.publish(answer);
      }
      await this.renewToken();
      await this.showOwn();
      if (published) {
        this.replies();
      }
      const shown = published ? document.getElementById(`comment-${answer.id}`) : document.getElementById(`own-${answer.id}`);
      this.say(published ? this.dataset.sentPublic : this.dataset.sent);
      this.goTo(shown);
    } catch {
      this.say("");
      this.showErrors([], this.dataset.failed);
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
  // What moderation() added (marked data-mod, or in its own box) stays (F-0211).
  async showOwn() {
    this.own.replaceChildren();
    for (const old of this.section.querySelectorAll(".own-note, .own-label, .own-controls, .own-edit")) {
      if (!old.closest("[data-mod], [data-moderation]")) {
        old.remove();
      }
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
        this.markOwn(published);
        if (comment.pendingText) {
          published.append(element("p", "own-note", this.dataset.editWaiting));
        }
        published.querySelector("[data-actions]")?.append(this.controls(comment, entry.token));
        continue;
      }

      // Removed by a moderator once published: its writer sees it, but can't change it (T-0180).
      // Approved but not on this page yet (another server, or a page from before): it shows on the
      // post shortly, and doesn't wait (F-0213).
      const removed = comment.status === "removed";
      const note = removed
        ? this.dataset.removed
        : comment.status === "rejected"
        ? this.dataset.rejected
        : comment.status === "approved"
        ? comment.pendingText ? this.dataset.editWaiting : this.dataset.publishedSoon
        : this.dataset.waiting;
      const waiting = comment.status === "pending" || (comment.status === "approved" && Boolean(comment.pendingText));
      const { article, actions } = this.article(comment, waiting ? "comment own waiting" : "comment own", note);
      article.id = `own-${id}`;
      this.markOwn(article);
      if (!removed) {
        actions.append(this.controls(comment, entry.token));
      }
      this.own.append(article);
    }
  }

  // "your comment" among the marks, before the time, which comes last.
  markOwn(article) {
    const meta = article.querySelector(".comment-meta");
    const label = element("span", "comment-mark own-label", this.dataset.yours);
    meta?.insertBefore(label, meta.querySelector(".time-toggle, time.comment-time"));
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
      // Two steps instead of a dialog: Delete, then Cancel or Delete for good (Cancel first, as in
      // the admin, U-01); Cancel puts the buttons back and the focus on Delete (F-0213).
      const before = [...box.children];
      const really = button(this.dataset.deleteConfirm, "btn small danger");
      const cancel = button(this.dataset.cancel);
      box.replaceChildren(cancel, really);
      really.focus();
      cancel.addEventListener("click", () => {
        box.replaceChildren(...before);
        remove.focus();
      });
      really.addEventListener("click", () => this.remove(comment, token));
    });
    return box;
  }

  // The focus back on a comment's own buttons, once showOwn() drew them again (F-0213).
  focusOwn(id) {
    const article = document.getElementById(`comment-${id}`) ?? document.getElementById(`own-${id}`);
    const target = article?.querySelector(".own-controls button") ?? this.form?.querySelector("textarea");
    target?.focus();
  }

  // Edit replaces the comment's text with the box while it's open (U-21), the box where the text
  // was; Cancel first, then Save (U-01). Its own check says an empty text in the site's words (U-04).

  edit(comment, token, box) {
    const article = box.closest("article");
    const form = element("form", "own-edit");
    form.noValidate = true;
    const label = element("label", null, this.dataset.editLabel);
    const area = element("textarea");
    area.id = `edit-${comment.id}`;
    label.htmlFor = area.id;
    area.required = true;
    area.rows = 5;
    area.maxLength = Number(this.dataset.textMax) || 1000;
    area.value = comment.pendingText ?? comment.text;
    // What's wrong with the edit, beside it and read with it (F-0213).
    const error = element("p", "notice error");
    error.id = `edit-error-${comment.id}`;
    error.hidden = true;
    const showError = (text) => {
      this.say("");
      error.textContent = text ?? "";
      error.hidden = false;
      area.setAttribute("aria-invalid", "true");
      area.setAttribute("aria-describedby", error.id);
      area.focus();
    };
    const save = element("button", "btn small primary", this.dataset.save);
    save.type = "submit";
    const cancel = button(this.dataset.cancel);
    const actions = element("div", "own-controls");
    actions.append(cancel, save);
    form.append(label, area, error, actions);
    const text = article?.querySelector(":scope > .comment-text");
    if (text) {
      text.after(form);
      text.hidden = true;
    } else {
      (article ?? box).append(form);
    }
    const close = () => {
      form.remove();
      if (text) {
        text.hidden = false;
      }
    };
    box.hidden = true;
    area.focus();
    if (!this.viewer?.moderator) {
      this.supply.prepare();
    }

    cancel.addEventListener("click", () => {
      close();
      box.hidden = false;
      box.querySelector("button")?.focus();
    });
    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      error.hidden = true;
      area.removeAttribute("aria-invalid");
      area.removeAttribute("aria-describedby");
      if (!area.value.trim()) {
        showError(this.dataset.textMissing);
        return;
      }
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
          showError(answer.message ?? this.dataset.failed);
          return;
        }
        close();
        // A moderator's edit of a published comment applies at once: the page shows it from the
        // answer, not from a reload another replica may answer with the old text (U-12).
        const published = document.getElementById(`comment-${comment.id}`);
        const atOnce = this.viewer?.moderator && answer.status === "approved" && !answer.pendingText;
        if (atOnce && published) {
          this.republish(published, answer.text);
        }
        await this.showOwn();
        this.say(atOnce ? this.dataset.savedPublic : this.dataset.saved);
        this.focusOwn(comment.id);
      } catch {
        showError(this.dataset.failed);
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
    this.counted();
    this.say(this.dataset.deleted);
    await this.showOwn();
    this.form?.querySelector("textarea")?.focus();
  }

  // A published comment its writer deleted, or a moderator removed: where a later comment follows, a
  // marker keeps its place, so the replies around it read, as the server shows it (T-0172, T-0180);
  // at the end it goes, and so do the markers it leaves last. Markers of the same kind in a row
  // become one line, "3 comments in a row were deleted by their writers" (U-23).
  leave(article, kind = "writer") {
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
      meta.append(element("span", "comment-deleted", kind === "moderator" ? this.dataset.removedByModerator : this.dataset.deletedByWriter));
      const time = article.querySelector("time.comment-time");
      if (time) {
        meta.append(time.closest(".time-toggle") ?? time);
      }
      article.className = "comment deleted";
      article.dataset.deleted = kind;
      article.dataset.count = "1";
      delete article.dataset.text;
      article.replaceChildren(meta);
      this.collapse(item);
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

  // A new marker joins the markers of its kind right before and after it, as one line that keeps
  // the first one's place and ID and says how many it stands for, without a time (U-23).
  collapse(item) {
    const markerOf = (li) => li?.querySelector(":scope > article.deleted");
    const kind = markerOf(item)?.dataset.deleted;
    let first = item;
    while (markerOf(first.previousElementSibling)?.dataset.deleted === kind) {
      first = first.previousElementSibling;
    }
    let count = Number(markerOf(first)?.dataset.count) || 1;
    while (markerOf(first.nextElementSibling)?.dataset.deleted === kind) {
      const following = first.nextElementSibling;
      count += Number(markerOf(following)?.dataset.count) || 1;
      if (!following.hidden) {
        first.hidden = false;
      }
      following.remove();
    }
    const marker = markerOf(first);
    if (!marker || count < 2) {
      return;
    }
    marker.dataset.count = String(count);
    const meta = element("p", "comment-meta");
    meta.append(element("span", "comment-deleted", fill(kind === "moderator" ? this.dataset.removedByModeratorMany : this.dataset.deletedByWriters, count.toLocaleString(siteLang()))));
    marker.replaceChildren(meta);
  }

  // The post's moderators: the comments waiting, with Approve and Reject, each sending the version
  // read, so what's approved is what they saw (F-0209); admins can also remove a published one, a
  // decision of its own (F-0210). Approving puts the comment, or its edit, in its place in the list.
  // What this adds lives in its own box or is marked data-mod, so showOwn() leaves it (F-0211).
  async moderation() {
    if (!this.viewer?.moderator) {
      return;
    }
    for (const article of this.section.querySelectorAll("[data-comment-list] article[data-comment-id]:not(.deleted)")) {
      this.removable(article);
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
        box.dataset.mod = "";
        box.append(element("p", "own-note", this.dataset.editOf), quoted(comment.pendingText));
        const controls = element("div", "own-controls");
        const approve = button(this.dataset.approveEdit, "btn small primary");
        const reject = button(this.dataset.rejectEdit);
        approve.addEventListener("click", () => this.decide(comment.id, "approve", box, undefined, comment.version, comment));
        reject.addEventListener("click", () => this.decide(comment.id, "reject", box, this.dataset.editRejectedMod, comment.version));
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
      approve.addEventListener("click", () => this.decide(comment.id, "approve", article, undefined, comment.version, comment));
      reject.addEventListener("click", () => this.decide(comment.id, "reject", article, undefined, comment.version));
      actions.append(approve, reject);
      article.dataset.mod = "";
      this.waitingBox.append(article);
    }
  }

  // An admin's Remove on a published comment that isn't their own, last among its buttons.
  removable(article) {
    const actions = article.querySelector("[data-actions]");
    if (!this.viewer?.admin || !actions || actions.querySelector(".remove") || this.mine?.has(article.dataset.commentId)) {
      return;
    }
    const remove = iconButton(this.dataset.remove, "reject", "btn small icon danger remove");
    remove.addEventListener("click", () => this.decide(article.dataset.commentId, "remove", article, this.dataset.removedMod));
    actions.append(remove);
  }

  async decide(id, decision, shown, done = this.dataset.rejectedMod, version = undefined, comment = undefined) {
    try {
      const response = await fetch("/visitor/comments/moderation", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        credentials: "same-origin",
        body: JSON.stringify({ post: this.post, id, decision, version }),
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
    // Approved, the comment or its edit shows in the list from what the page read, which is what
    // was approved (F-0209), not from a reload another replica may answer without it (U-11).
    if (decision === "approve" && comment) {
      const published = document.getElementById(`comment-${id}`);
      shown?.remove();
      let article = published;
      if (published && comment.pendingText) {
        this.republish(published, comment.pendingText);
      } else if (!published) {
        article = this.publish({ ...comment, text: comment.pendingText ?? comment.text });
        this.replies();
        this.removable(article);
      }
      this.say(this.dataset.approved);
      this.goTo(article);
      return;
    }
    if (shown?.matches("[data-comment-list] article")) {
      this.leave(shown, "moderator");
      this.counted();
    } else {
      shown?.remove();
    }
    this.say(done);
    this.form?.querySelector("textarea")?.focus();
  }
}

if (!customElements.get("ws-comments")) {
  customElements.define("ws-comments", WsComments);
}
