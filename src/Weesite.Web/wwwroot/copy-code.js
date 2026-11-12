// Adds a Copy button to each code block's bar (ADR 0006). Only an enhancement: without
// JavaScript, or without clipboard access, the code is still there to select. Its words come from
// the content's data-copy-* attributes, in the site's language (ADR 0008), English without them.

const label = (button, text) => {
  button.querySelector("span").textContent = text;
};

for (const block of document.querySelectorAll(".code-block")) {
  const bar = block.querySelector(".bar");
  const code = block.querySelector("pre code");
  if (!bar || !code || !navigator.clipboard || bar.querySelector("button")) {
    continue;
  }

  const words = { copy: "Copy", copied: "Copied", copiedStatus: "Code copied", failed: "Select and copy", failedStatus: "Copying isn't allowed here; select the code instead" };
  const source = block.closest("[data-copy]")?.dataset;
  if (source) {
    words.copy = source.copy ?? words.copy;
    words.copied = source.copied ?? words.copied;
    words.copiedStatus = source.copiedStatus ?? words.copiedStatus;
    words.failed = source.copyFailed ?? words.failed;
    words.failedStatus = source.copyFailedStatus ?? words.failedStatus;
  }

  const button = document.createElement("button");
  button.type = "button";
  button.innerHTML = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><rect x="9" y="9" width="12" height="12" rx="2"></rect><path d="M5 15V5a2 2 0 0 1 2-2h10"></path></svg><span></span>';
  label(button, words.copy);
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
