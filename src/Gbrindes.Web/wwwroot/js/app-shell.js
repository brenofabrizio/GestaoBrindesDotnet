(() => {
    "use strict";

    const tokenKey = "gbrindes.auth.token";
    const base = location.pathname.startsWith("/gbrindes") ? "/gbrindes" : "";

    document.getElementById("logoutBtn")?.addEventListener("click", async function () {
        this.disabled = true;
        const token = localStorage.getItem(tokenKey);
        try {
            if (token) {
                await fetch(`${base}/api/v1/auth/logout`, {
                    method: "POST",
                    headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
                });
            }
        } finally {
            localStorage.removeItem(tokenKey);
            window.OfflineQueue?.setAccessToken(null);
            location.href = `${base}/login`;
        }
    });

    (async () => {
        const token = localStorage.getItem(tokenKey);
        if (!token) return;
        try {
            const response = await fetch(`${base}/api/v1/auth/me`, {
                headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
            });
            if (!response.ok) throw new Error("auth");
            const me = await response.json();
            const permissions = new Set(me.permissions || []);
            const admin = me.isAdmin === true || me.is_admin === true;
            document.querySelectorAll("[data-permission]").forEach(link => {
                if (!admin && !permissions.has(link.dataset.permission)) link.hidden = true;
            });
        } catch (_) {
            document.querySelectorAll("[data-permission]").forEach(link => { link.hidden = true; });
        }
    })();
})();
