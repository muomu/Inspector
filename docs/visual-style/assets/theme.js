(() => {
  const root = document.documentElement;
  const KEY = "gi-visual-theme";
  const params = new URLSearchParams(window.location.search);
  const requested = params.get("theme");

  let stored = null;
  try {
    stored = window.localStorage.getItem(KEY);
  } catch (_) {
    stored = null;
  }

  const initial = requested === "dark" || requested === "light" ? requested : stored;
  if (initial === "dark") {
    root.setAttribute("data-theme", "dark");
  }

  const buttons = document.querySelectorAll("[data-theme-toggle]");

  const sync = () => {
    const dark = root.getAttribute("data-theme") === "dark";
    buttons.forEach((button) => {
      button.textContent = dark ? "浅色" : "深色";
      button.setAttribute("aria-pressed", String(dark));
      button.setAttribute("aria-label", dark ? "切换到浅色主题" : "切换到深色主题");
    });
  };

  buttons.forEach((button) => {
    button.addEventListener("click", () => {
      const dark = root.getAttribute("data-theme") === "dark";
      if (dark) {
        root.removeAttribute("data-theme");
      } else {
        root.setAttribute("data-theme", "dark");
      }
      try {
        window.localStorage.setItem(KEY, dark ? "light" : "dark");
      } catch (_) {
        /* file:// 下 localStorage 可能不可用，忽略 */
      }
      sync();
    });
  });

  sync();
})();
