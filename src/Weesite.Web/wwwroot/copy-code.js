// Adds a Copy button to each code block's bar (ADR 0006). Only an enhancement: without
// JavaScript, or without clipboard access, the code is still there to select. Its words come from
// the content's data-copy-* attributes, in the site's language (ADR 0008), English without them.
// Each button is named after its block (U-19): by its file name, or its language, when no other
// block on the page has the same; otherwise by its number ("Copy code, block 2").

const label = (button, text) => {
  button.querySelector("span").textContent = text;
};

const fill = (pattern, value) => pattern.replace("{0}", value);

const blocks = [...document.querySelectorAll(".code-block")];
const fileOf = (block) => block.querySelector(".bar .file")?.textContent.trim() || null;
const languageOf = (block) => block.querySelector("pre.code")?.dataset.language || null;
const counts = new Map();
for (const block of blocks) {
  for (const key of [`file:${fileOf(block)}`, `language:${languageOf(block)}`]) {
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
}

const nameOf = (block, index, words) => {
  const file = fileOf(block);
  if (file && counts.get(`file:${file}`) === 1) {
    return fill(words.copyFile, file);
  }
  const language = languageOf(block);
  if (language && counts.get(`language:${language}`) === 1) {
    return fill(words.copyNamed, language);
  }
  return fill(words.copyNumbered, String(index + 1));
};

for (const [index, block] of blocks.entries()) {
  const bar = block.querySelector(".bar");
  const code = block.querySelector("pre code");
  if (!bar || !code || !navigator.clipboard || bar.querySelector("button")) {
    continue;
  }

  const words = {
    copy: "Copy",
    copyNamed: "Copy the {0} code",
    copyFile: "Copy {0}",
    copyNumbered: "Copy code, block {0}",
    copied: "Copied",
    copiedStatus: "Code copied",
    failed: "Select and copy",
    failedStatus: "Copying isn't allowed here; select the code instead",
  };
  const source = block.closest("[data-copy]")?.dataset;
  if (source) {
    words.copy = source.copy ?? words.copy;
    words.copyNamed = source.copyNamed ?? words.copyNamed;
    words.copyFile = source.copyFile ?? words.copyFile;
    words.copyNumbered = source.copyNumbered ?? words.copyNumbered;
    words.copied = source.copied ?? words.copied;
    words.copiedStatus = source.copiedStatus ?? words.copiedStatus;
    words.failed = source.copyFailed ?? words.failed;
    words.failedStatus = source.copyFailedStatus ?? words.failedStatus;
  }

  const button = document.createElement("button");
  button.type = "button";
  button.innerHTML = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><rect x="9" y="9" width="12" height="12" rx="2"></rect><path d="M5 15V5a2 2 0 0 1 2-2h10"></path></svg><span></span>';
  label(button, words.copy);
  // The name starts with the visible word (WCAG 2.5.3); the status says what happened.
  button.setAttribute("aria-label", nameOf(block, index, words));
  const status = document.createElement("span");
  status.className = "visually-hidden";
  status.setAttribute("role", "status");
  button.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(code.innerText);
      label(button, words.copied);
      status.textContent = words.copiedStatus;
    } catch {
      label(button, words.failed);
      status.textContent = words.failedStatus;
    }
    setTimeout(() => {
      label(button, words.copy);
      status.textContent = "";
    }, 2000);
  });
  bar.append(button, status);
}
