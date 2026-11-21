(() => {
  if (!('serviceWorker' in navigator)) return;
  const base = '/gbrindes';
  navigator.serviceWorker.register(`${base}/sw.js`, { scope: `${base}/` }).catch(() => {});
})();
