// The landing pages (/, /upgrade, /account) show at once, without what the identity's host says it has now: asking the
// host can take a while. This asks for the page again with it (UpgradePage.PlanNowHeader) and swaps in the parts that
// show it. If the host cannot say, the page stays as it is, as it would have shown anyway.
(() => {
  const pending = document.querySelectorAll('[data-plan-now="pending"]');
  if (pending.length === 0) return;
  fetch(location.href, { headers: { "X-Plan-Now": "1" }, cache: "no-store" })
    .then((response) => (response.ok ? response.text() : null))
    .then((html) => {
      if (!html) return;
      const ready = new DOMParser().parseFromString(html, "text/html").querySelectorAll('[data-plan-now="ready"]');
      // A different page (the session ended, say) has none, or not the same ones
      if (ready.length !== pending.length) return;
      pending.forEach((region, i) => region.replaceWith(document.importNode(ready[i], true)));
    })
    .catch(() => {});
})();
