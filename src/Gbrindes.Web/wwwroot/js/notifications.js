(() => {
    const page = document.getElementById("notifications-page");
    if (!page) return;

    const loginForm = document.getElementById("notifications-login-form");
    const email = document.getElementById("notifications-email");
    const password = document.getElementById("notifications-password");
    const loginButton = document.getElementById("notifications-login");
    const session = document.getElementById("notifications-session");
    const userLabel = document.getElementById("notifications-user-label");
    const logoutButton = document.getElementById("notifications-logout");
    const message = document.getElementById("notifications-message");
    const results = document.getElementById("notifications-results");
    const list = document.getElementById("notifications-list");
    const pagination = document.getElementById("notifications-pagination");
    const unread = document.getElementById("notifications-unread");
    const readAllButton = document.getElementById("notifications-read-all");
    const refreshButton = document.getElementById("notifications-refresh");
    let token = null;
    let pageNumber = 1;
    let busy = false;

    function say(text, error = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", error);
        message.classList.toggle("text-secondary", !error);
    }

    function setBusy(value) {
        busy = value;
        for (const button of page.querySelectorAll("button")) button.disabled = value;
        unread.disabled = value;
    }

    async function readResponse(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const errors = data.errors && Object.values(data.errors).flat().join(" ");
            const error = new Error(errors || data.message || data.title || data.error?.message || `Falha na requisição (${response.status}).`);
            error.status = response.status;
            throw error;
        }
        return data;
    }

    async function api(path, init = {}) {
        return readResponse(await fetch(path, {
            cache: "no-store",
            ...init,
            headers: { Authorization: `Bearer ${token}`, Accept: "application/json", ...(init.headers || {}) }
        }));
    }

    function formatDate(value) {
        if (!value) return "";
        const date = new Date(value);
        return Number.isNaN(date.valueOf()) ? String(value) : date.toLocaleString("pt-BR");
    }

    function render(rows) {
        list.replaceChildren();
        if (!rows.length) {
            const empty = document.createElement("p");
            empty.className = "text-secondary";
            empty.textContent = "Nenhuma notificação.";
            list.append(empty);
            return;
        }
        for (const notification of rows) {
            const card = document.createElement("article");
            card.className = `card card-body ${notification.read_at ? "" : "border-primary"}`;
            const header = document.createElement("div");
            header.className = "d-flex justify-content-between gap-3";
            const subject = document.createElement("strong");
            subject.textContent = notification.subject || "Notificação";
            header.append(subject);
            if (!notification.read_at) {
                const badge = document.createElement("span");
                badge.className = "badge text-bg-primary flex-shrink-0";
                badge.textContent = "Nova";
                header.append(badge);
            }
            const date = document.createElement("div");
            date.className = "small text-secondary mt-1";
            date.textContent = formatDate(notification.created_at);
            const body = document.createElement("p");
            body.className = "mb-2 mt-2";
            body.textContent = notification.body || "";
            card.append(header, date, body);
            if (notification.link_url) {
                const link = document.createElement("a");
                link.className = "small";
                link.href = notification.link_url;
                link.textContent = "Abrir operação";
                link.addEventListener("click", () => markRead(notification.id));
                card.append(link);
            } else if (!notification.read_at) {
                const button = document.createElement("button");
                button.type = "button";
                button.className = "btn btn-sm btn-outline-secondary align-self-start";
                button.textContent = "Marcar como lida";
                button.addEventListener("click", () => markRead(notification.id));
                card.append(button);
            }
            list.append(card);
        }
    }

    function renderPagination(meta) {
        pagination.replaceChildren();
        if (!meta || meta.last_page <= 1) return;
        const group = document.createElement("div");
        group.className = "d-flex align-items-center gap-2";
        const previous = document.createElement("button");
        previous.type = "button";
        previous.className = "btn btn-sm btn-outline-secondary";
        previous.textContent = "Anterior";
        previous.disabled = pageNumber <= 1;
        previous.addEventListener("click", () => load(pageNumber - 1));
        const label = document.createElement("span");
        label.className = "small text-secondary";
        label.textContent = `Página ${meta.page} de ${meta.last_page}`;
        const next = document.createElement("button");
        next.type = "button";
        next.className = "btn btn-sm btn-outline-secondary";
        next.textContent = "Próxima";
        next.disabled = pageNumber >= meta.last_page;
        next.addEventListener("click", () => load(pageNumber + 1));
        group.append(previous, label, next);
        pagination.append(group);
    }

    async function load(nextPage = 1) {
        if (!token || busy) return;
        setBusy(true);
        say("Carregando notificações…");
        pageNumber = Math.max(1, nextPage);
        try {
            const query = new URLSearchParams({ page: String(pageNumber), per_page: "25", unread: unread.checked ? "true" : "false" });
            const response = await api(`/api/v1/notifications?${query}`);
            render(Array.isArray(response.data) ? response.data : []);
            renderPagination(response.meta);
            say(response.meta?.unread ? `${response.meta.unread} notificação(ões) não lida(s).` : "Notificações atualizadas.");
        } catch (error) {
            say(error.message || "Não foi possível carregar as notificações.", true);
        } finally {
            setBusy(false);
        }
    }

    async function markRead(id) {
        if (!token || busy) return;
        try {
            await api(`/api/v1/notifications/${encodeURIComponent(id)}/read`, { method: "POST" });
            await load(pageNumber);
        } catch (error) {
            say(error.message || "Não foi possível marcar a notificação.", true);
        }
    }

    loginForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !loginForm.reportValidity()) return;
        setBusy(true);
        say("Validando acesso…");
        try {
            const data = await readResponse(await fetch("/api/v1/auth/login", {
                method: "POST",
                cache: "no-store",
                headers: { "Content-Type": "application/json", Accept: "application/json" },
                body: JSON.stringify({ email: email.value, password: password.value })
            }));
            password.value = "";
            token = data.accessToken || null;
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de continuar.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            loginForm.classList.add("d-none");
            session.classList.remove("d-none");
            results.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            setBusy(false);
            await load();
        } catch (error) {
            token = null;
            say(error.message || "Não foi possível entrar.", true);
        } finally {
            setBusy(false);
        }
    });

    readAllButton.addEventListener("click", async () => {
        if (!token || busy) return;
        setBusy(true);
        try {
            await api("/api/v1/notifications/read-all", { method: "POST" });
            await load(1);
        } catch (error) {
            say(error.message || "Não foi possível marcar as notificações.", true);
        } finally {
            setBusy(false);
        }
    });

    unread.addEventListener("change", () => load(1));
    refreshButton.addEventListener("click", () => load(pageNumber));
    logoutButton.addEventListener("click", async () => {
        if (busy || !token) return;
        setBusy(true);
        try { await api("/api/v1/auth/logout", { method: "POST" }); } catch { /* Limpeza local abaixo. */ }
        token = null;
        list.replaceChildren();
        pagination.replaceChildren();
        results.classList.add("d-none");
        session.classList.add("d-none");
        loginForm.classList.remove("d-none");
        say("Sessão encerrada.");
        setBusy(false);
    });
})();
