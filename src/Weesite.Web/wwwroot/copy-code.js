// Adds a Copy button to each code block's bar (ADR 0006). Only an enhancement: without
// JavaScript, or without clipboard access, the code is still there to select.

const label = (button, text) => {
  button.querySelector("span").textContent = text;
};

for (const block of document.querySelectorAll(".code-block")) {
  const bar = block.querySelector(".bar");
  const code = block.querySelector("pre code");
  if (!bar || !code || !navigator.clipboard || bar.querySelector("button")) {
    continue;
  }

  const button = document.createElement("button");
  button.type = "button";
  button.innerHTML = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><rect x="9" y="9" width="12" height="12" rx="2"></rect><path d="M5 15V5a2 2 0 0 1 2-2h10"></path></svg><span>Copy</span>';
  const status = document.createElement("span");
  status.className = "visually-hidden";
  status.setAttribute("role", "status");
  button.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(code.innerText);
      label(button, "Copied");
      status.textContent = "Code copied";
    } catch {
      label(button, "Select and copy");
      status.textContent = "Copying isn't allowed here; select the code instead";
    }
    setTimeout(() => {
      label(button, "Copy");
      status.textContent = "";
    }, 2000);
  });
  bar.append(button, status);
}
