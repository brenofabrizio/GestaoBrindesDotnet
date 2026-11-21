(() => {
    const page = document.getElementById("events-page");
    if (!page) return;
    const loginForm = document.getElementById("events-login-form");
    const email = document.getElementById("events-email");
    const password = document.getElementById("events-password");
    const loginButton = document.getElementById("events-login");
    const session = document.getElementById("events-session");
    const userLabel = document.getElementById("events-user-label");
    const logoutButton = document.getElementById("events-logout");
    const results = document.getElementById("events-results");
    const list = document.getElementById("events-list");
    const message = document.getElementById("events-message");
    const createForm = document.getElementById("events-create-form");
    const search = document.getElementById("events-search");
    let token = null;
    let busy = false;

    function say(text, error = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", error);
        message.classList.toggle("text-secondary", !error);
    }
    async function readResponse(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const errors = data.errors && Object.values(data.errors).flat().join(" ");
            throw new Error(errors || data.message || data.title || data.error?.message || data.error || `Falha na requisição (${response.status}).`);
        }
        return data;
    }
    async function api(path, init = {}) {
        const response = await fetch(path, { cache: "no-store", ...init, headers: { Authorization: `Bearer ${token}`, Accept: "application/json", ...(init.headers || {}) } });
        return readResponse(response);
    }
    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        logoutButton.disabled = value;
        for (const button of page.querySelectorAll("button[type=submit], #events-refresh")) button.disabled = value;
    }
    function render(rows) {
        list.replaceChildren();
        if (!rows.length) {
            const empty = document.createElement("p");
            empty.className = "text-secondary";
            empty.textContent = "Nenhum evento encontrado.";
            list.append(empty);
            return;
        }
        for (const event of rows) {
            const column = document.createElement("div");
            column.className = "col-12 col-lg-6";
            const card = document.createElement("article");
            card.className = "card card-body h-100";
            const title = document.createElement("h3");
            title.className = "h5";
            const detailLink = document.createElement("a");
            detailLink.href = `/events/${encodeURIComponent(event.id)}`;
            detailLink.textContent = event.name || "Evento";
            title.append(detailLink);
            const details = document.createElement("p");
            details.className = "small text-secondary mb-2";
            details.textContent = [event.status_label || event.status, event.venue, event.starts_on, event.ends_on].filter(Boolean).join(" · ");
            const description = document.createElement("p");
            description.className = "mb-0";
            description.textContent = event.description || "Sem descrição.";
            card.append(title, details, description);
            column.append(card);
            list.append(column);
        }
    }
    async function loadEvents() {
        if (!token || busy) return;
        setBusy(true);
        say("Carregando eventos…");
        try {
            const query = new URLSearchParams({ per_page: "100" });
            if (search.value.trim()) query.set("q", search.value.trim());
            const response = await api(`/api/events?${query}`);
            render(Array.isArray(response.data) ? response.data : []);
            say("Eventos atualizados. Operações de gestão são aplicadas pela API conforme suas permissões.");
        } catch (error) { say(error.message || "Não foi possível carregar os eventos.", true); }
        finally { setBusy(false); }
    }
    loginForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy) return;
        setBusy(true);
        say("Validando acesso…");
        try {
            const response = await fetch("/api/v1/auth/login", { method: "POST", cache: "no-store", headers: { "Content-Type": "application/json", Accept: "application/json" }, body: JSON.stringify({ email: email.value, password: password.value }) });
            const data = await readResponse(response);
            password.value = "";
            token = data.accessToken || null;
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de continuar.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            loginForm.classList.add("d-none");
            session.classList.remove("d-none");
            results.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            setBusy(false);
            await loadEvents();
        } catch (error) {
            token = null;
            say(error.message || "Não foi possível entrar.", true);
        } finally { setBusy(false); }
    });
    createForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token) return;
        setBusy(true);
        say("Criando evento…");
        const payload = {
            name: document.getElementById("event-name").value.trim(),
            venue: document.getElementById("event-venue").value.trim() || null,
            starts_on: document.getElementById("event-start").value || null,
            ends_on: document.getElementById("event-end").value || null,
            description: document.getElementById("event-description").value.trim() || null
        };
        try {
            await api("/api/v1/events", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
            createForm.reset();
            say("Evento planejado criado.");
            setBusy(false);
            await loadEvents();
        } catch (error) { say(error.message || "Não foi possível criar o evento.", true); }
        finally { setBusy(false); }
    });
    document.getElementById("events-refresh").addEventListener("click", loadEvents);
    search.addEventListener("keydown", event => { if (event.key === "Enter") { event.preventDefault(); loadEvents(); } });
    logoutButton.addEventListener("click", async () => {
        if (busy || !token) return;
        setBusy(true);
        try { await fetch("/api/v1/auth/logout", { method: "POST", cache: "no-store", headers: { Authorization: `Bearer ${token}`, Accept: "application/json" } }); }
        finally {
            token = null;
            list.replaceChildren();
            results.classList.add("d-none");
            session.classList.add("d-none");
            loginForm.classList.remove("d-none");
            say("Sessão encerrada.");
            setBusy(false);
        }
    });
})();
