// The admin's menu on a phone (U-35): the sidebar becomes a top bar, and its Menu button shows and
// hides the navigation, the theme switch and Sign out below it. The button's aria-expanded is the
// state admin.css reads, so the button says what the menu does. Escape closes the menu and gives
// the focus back to the button. On a wider screen the button isn't shown and the menu always is.

const button = document.querySelector(".admin-nav .menu-toggle");
const menu = button ? document.getElementById(button.getAttribute("aria-controls") ?? "") : null;

if (button && menu) {
  const isOpen = () => button.getAttribute("aria-expanded") === "true";

  button.addEventListener("click", () => {
    button.setAttribute("aria-expanded", String(!isOpen()));
  });

  // Only while the focus is in the menu or on its button: elsewhere Escape belongs to what has the
  // focus, such as a dialog or the Markdown editor.
  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape" || event.defaultPrevented || !isOpen()) {
      return;
    }
    if (!(event.target instanceof Node) || !(event.target === button || menu.contains(event.target))) {
      return;
    }
    button.setAttribute("aria-expanded", "false");
    button.focus();
    event.preventDefault();
  });
}
