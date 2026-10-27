// The account menu is a <details>: it opens and closes without this. This closes it the way menus close
// everywhere else, on a click outside it and on Escape (focus goes back to the avatar).
(() => {
  const menus = () => document.querySelectorAll("details.account-menu[open]");
  document.addEventListener("click", (event) => {
    menus().forEach((menu) => {
      if (!menu.contains(event.target)) menu.open = false;
    });
  });
  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") return;
    menus().forEach((menu) => {
      menu.open = false;
      menu.querySelector("summary").focus();
    });
  });
})();
