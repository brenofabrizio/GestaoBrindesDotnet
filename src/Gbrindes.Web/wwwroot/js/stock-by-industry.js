(() => {
    const byId = id => document.getElementById(id);
    const loginForm = byId("stock-by-industry-login-form");
    if (!loginForm) return;

    const emailInput = byId("stock-by-industry-email");
    const passwordInput = byId("stock-by-industry-password");
    const loginButton = byId("stock-by-industry-login");
    const sessionPanel = byId("stock-by-industry-session");
    const userLabel = byId("stock-by-industry-user-label");
    const logoutButton = byId("stock-by-industry-logout");
    const results = byId("stock-by-industry-results");
    const message = byId("stock-by-industry-message");
    const summaryRows = byId("stock-by-industry-rows");
    const positionRows = byId("stock-by-industry-positions");
    const numberFormat = new Intl.NumberFormat("pt-BR");
    let token = null;
    let busy = false;

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        logoutButton.disabled = value;
    }

    async function readResponse(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const error = new Error(data.error?.message || data.message || data.detail || data.title || `Falha na requisição (${response.status}).`);
            error.status = response.status;
            throw error;
        }
        return data;
    }

    async function api(path, init = {}) {
        const response = await fetch(path, {
            cache: "no-store",
            ...init,
            headers: { Authorization: `Bearer ${token}`, Accept: "application/json", ...(init.headers || {}) }
        });
        return readResponse(response);
    }

    function appendCell(row, text, className = "") {
        const cell = document.createElement("td");
        if (className) cell.className = className;
        cell.textContent = text;
        row.append(cell);
    }

    function renderSummary(rows) {
        summaryRows.replaceChildren();
        if (!rows.length) {
            const row = document.createElement("tr");
            const cell = document.createElement("td");
            cell.colSpan = 5;
            cell.className = "text-center text-secondary py-4";
            cell.textContent = "Nenhum saldo encontrado no escopo disponível.";
            row.append(cell);
            summaryRows.append(row);
            return;
        }
        for (const entry of rows) {
            const row = document.createElement("tr");
            appendCell(row, entry.industry?.name || "—");
            appendCell(row, `${entry.item?.code || ""} — ${entry.item?.name || "Brinde"}`);
            appendCell(row, numberFormat.format(entry.received || 0), "text-end");
            appendCell(row, numberFormat.format(entry.withdrawn || 0), "text-end");
            appendCell(row, numberFormat.format(entry.saldo || 0), "text-end fw-semibold");
            summaryRows.append(row);
        }
    }

    function renderPositions(rows) {
        positionRows.replaceChildren();
        if (!rows.length) {
            const row = document.createElement("tr");
            const cell = document.createElement("td");
            cell.colSpan = 3;
            cell.className = "text-center text-secondary py-4";
            cell.textContent = "Não há unidades disponíveis por local.";
            row.append(cell);
            positionRows.append(row);
            return;
        }
        for (const position of rows) {
            const row = document.createElement("tr");
            appendCell(row, `${position.location?.kind || "Local"} — ${position.location?.name || "—"}`);
            appendCell(row, `${position.item?.code || ""} — ${position.item?.name || "Brinde"}`);
            appendCell(row, numberFormat.format(position.qty || 0), "text-end fw-semibold");
            positionRows.append(row);
        }
    }

    async function load() {
        setBusy(true);
        results.classList.remove("d-none");
        setMessage("Carregando saldo e posições…");
        try {
            const [summary, positions] = await Promise.all([
                api("/api/v1/stock/by-industry"),
                api("/api/v1/stock/positions")
            ]);
            renderSummary(Array.isArray(summary.data) ? summary.data : []);
            renderPositions(Array.isArray(positions.data) ? positions.data : []);
            setMessage("Consulta atualizada. Nenhum saldo foi alterado.");
        } catch (error) {
            results.classList.add("d-none");
            setMessage(error.message || "Não foi possível carregar o saldo.", true);
            if (error.status === 401) clearSession();
        } finally {
            setBusy(false);
        }
    }

    function clearSession() {
        token = null;
        summaryRows.replaceChildren();
        positionRows.replaceChildren();
        results.classList.add("d-none");
        sessionPanel.classList.add("d-none");
        loginForm.classList.remove("d-none");
        passwordInput.value = "";
    }

    loginForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy) return;
        setBusy(true);
        setMessage("Validando acesso…");
        try {
            const response = await fetch("/api/v1/auth/login", {
                method: "POST",
                cache: "no-store",
                headers: { "Content-Type": "application/json", Accept: "application/json" },
                body: JSON.stringify({ email: emailInput.value, password: passwordInput.value })
            });
            const data = await readResponse(response);
            passwordInput.value = "";
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de consultar estoque.");
            token = data.accessToken || null;
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            if (!Array.isArray(data.permissions) || !data.permissions.includes("stock.view")) {
                throw new Error("Esta consulta exige a permissão stock.view.");
            }
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
        } catch (error) {
            token = null;
            setMessage(error.message || "Não foi possível autenticar.", true);
            setBusy(false);
            return;
        }
        await load();
    });

    logoutButton.addEventListener("click", async () => {
        if (busy) return;
        setBusy(true);
        try {
            await api("/api/v1/auth/logout", { method: "POST" });
        } catch { /* A sessão local também deve ser removida se a API estiver indisponível. */ }
        clearSession();
        setMessage("Sessão encerrada.");
        setBusy(false);
    });
})();
