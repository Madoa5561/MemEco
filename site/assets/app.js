/* MemEco — official site (vanilla, no dependencies) */
(() => {
  "use strict";

  /* --------------------------------------------------- sticky header */
  const header = document.getElementById("site-header");
  const onScroll = () => {
    if (!header) return;
    header.classList.toggle("is-scrolled", window.scrollY > 8);
  };
  onScroll();
  window.addEventListener("scroll", onScroll, { passive: true });

  /* --------------------------------------------------- mobile nav */
  const toggle = document.getElementById("nav-toggle");
  const nav = document.getElementById("nav");
  if (toggle && nav) {
    const setOpen = (open) => {
      nav.classList.toggle("is-open", open);
      toggle.setAttribute("aria-expanded", String(open));
      toggle.setAttribute("aria-label", open ? "メニューを閉じる" : "メニューを開く");
    };
    toggle.addEventListener("click", () => {
      setOpen(!nav.classList.contains("is-open"));
    });
    nav.querySelectorAll("a").forEach((a) => {
      a.addEventListener("click", () => setOpen(false));
    });
    window.addEventListener("keydown", (e) => {
      if (e.key === "Escape") setOpen(false);
    });
  }

  /* --------------------------------------------------- reveal on scroll */
  const revealTargets = document.querySelectorAll(
    ".section-head, .card, .step, .safety-card, .screen, .metric, .download-card, .start-steps li, .faq details, .cta-inner"
  );

  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // JS が動いている場合のみ「隠してから出す」演出を有効にする。
  const revealAll = () =>
    revealTargets.forEach((el) => el.classList.add("is-visible"));

  if (!reduceMotion && "IntersectionObserver" in window) {
    revealTargets.forEach((el, i) => {
      el.classList.add("reveal");
      el.style.transitionDelay = `${Math.min(i % 4, 3) * 40}ms`;
    });

    const io = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            entry.target.classList.add("is-visible");
            io.unobserve(entry.target);
          }
        });
      },
      { rootMargin: "0px 0px -8% 0px", threshold: 0.05 }
    );
    revealTargets.forEach((el) => io.observe(el));

    // スクロールが飛んだ場合や IO が発火しなかった場合の保険。
    // 一定時間後には必ず表示する（コンテンツが消えたままにならない）。
    window.setTimeout(revealAll, 2200);
  }

  /* --------------------------------------------------- smooth anchor focus */
  document.querySelectorAll('a[href^="#"]').forEach((a) => {
    a.addEventListener("click", () => {
      const id = a.getAttribute("href");
      if (!id || id === "#") return;
      const target = document.querySelector(id);
      if (target) {
        target.setAttribute("tabindex", "-1");
        target.focus({ preventScroll: true });
      }
    });
  });

  /* --------------------------------------------------- current year fallback */
  const copy = document.querySelector(".footer-copy");
  if (copy && copy.textContent.includes("2026")) {
    copy.textContent = copy.textContent.replace("2026", String(new Date().getFullYear()));
  }
})();
