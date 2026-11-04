(() => {
  const root = document.documentElement;
  const key = "gi-theme";
  const params = new URLSearchParams(window.location.search);
  const requested = params.get("theme");

  let memoryTheme = null;
  try {
    memoryTheme = window.localStorage.getItem(key);
  } catch (_) {
    memoryTheme = null;
  }

  // URL 的 ?theme=dark|light 只在首次加载时生效，并记忆到 localStorage；
  // 之后增强导航 / 刷新都以记忆值为准，避免分享链接反过来覆盖用户切换。
  if (requested === "dark" || requested === "light") {
    memoryTheme = requested;
    try {
      window.localStorage.setItem(key, requested);
    } catch (_) {
      /* 隐私模式下只保留内存值 */
    }
  }

  const readTheme = () => {
    try {
      return window.localStorage.getItem(key) ?? memoryTheme;
    } catch (_) {
      return memoryTheme;
    }
  };

  const writeTheme = (value) => {
    memoryTheme = value;
    try {
      window.localStorage.setItem(key, value);
    } catch (_) {
      /* 隐私模式下忽略 */
    }
  };

  const syncToggles = () => {
    const dark = root.getAttribute("data-theme") === "dark";
    document.querySelectorAll("[data-theme-toggle]").forEach((button) => {
      button.setAttribute("aria-pressed", dark ? "true" : "false");
      button.setAttribute("aria-label", dark ? "切换为浅色" : "切换为深色");
    });
  };

  const applyStoredTheme = () => {
    if (readTheme() === "dark") {
      root.setAttribute("data-theme", "dark");
    } else {
      root.removeAttribute("data-theme");
    }

    syncToggles();
  };

  applyStoredTheme();

  // 增强导航会从 SSR 重新同步 <html> 属性，data-theme 可能被清掉；
  // 事件在 window.Blazor 上派发（不是 document），两个都挂 + 属性观察兜底。
  const onEnhancedLoad = () => applyStoredTheme();
  document.addEventListener("enhancedload", onEnhancedLoad);

  const attachBlazorHook = () => {
    if (window.Blazor && typeof window.Blazor.addEventListener === "function") {
      window.Blazor.addEventListener("enhancedload", onEnhancedLoad);
    }
  };

  attachBlazorHook();
  window.addEventListener("load", attachBlazorHook);

  new MutationObserver(() => {
    const shouldBeDark = readTheme() === "dark";
    const isDark = root.getAttribute("data-theme") === "dark";
    if (shouldBeDark !== isDark) {
      applyStoredTheme();
    } else {
      syncToggles();
    }
  }).observe(root, { attributes: true, attributeFilter: ["data-theme"] });

  // 事件委托：Blazor 增强导航替换 DOM 后按钮仍可用，不依赖一次性绑定。
  document.addEventListener("click", (event) => {
    const button = event.target.closest("[data-theme-toggle]");
    if (!button) {
      return;
    }

    writeTheme(root.getAttribute("data-theme") === "dark" ? "light" : "dark");
    applyStoredTheme();

    // 用户手动切换后，URL 里的 ?theme= 不再强制刷新时的主题。
    const url = new URL(window.location.href);
    if (url.searchParams.has("theme")) {
      url.searchParams.delete("theme");
      window.history.replaceState({}, "", `${url.pathname}${url.search}${url.hash}`);
    }
  });

  // 跳到正文：显式把焦点放到 <main tabindex="-1">，避免部分浏览器只滚动不移动焦点。
  document.addEventListener("click", (event) => {
    const skip = event.target.closest(".skip");
    if (!skip) {
      return;
    }

    const target = document.querySelector(skip.getAttribute("href"));
    if (!target) {
      return;
    }

    event.preventDefault();
    target.focus({ preventScroll: true });
    target.scrollIntoView();
    window.history.replaceState(null, "", skip.getAttribute("href"));
  });

  // 增强导航不会重建布局；上下文表单提交时按当前路径纠正 action，避免仍提交到旧页。
  document.addEventListener("submit", (event) => {
    const form = event.target;
    if (form instanceof HTMLFormElement && form.classList.contains("context-form")) {
      form.action = window.location.pathname;
    }
  });
})();
