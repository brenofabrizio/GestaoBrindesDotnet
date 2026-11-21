(() => {
    const byId = id => document.getElementById(id);
    const loginForm = byId("stock-journal-login-form");
    if (!loginForm) return;

    const emailInput = byId("stock-journal-email");
    const passwordInput = byId("stock-journal-password");
    const loginButton = byId("stock-journal-login");
    const sessionPanel = byId("stock-journal-session");
    const userLabel = byId("stock-journal-user-label");
    const logoutButton = byId("stock-journal-logout");
    const filters = byId("stock-journal-filters");
    const results = byId("stock-journal-results");
    const rowsBody = byId("stock-journal-rows");
    const message = byId("stock-journal-message");
    const searchButton = byId("stock-journal-search");
    const previousButton = byId("stock-journal-previous");
    const nextButton = byId("stock-journal-next");
    const pageLabel = byId("stock-journal-page-label");
    const reconcilePanel = byId("stock-journal-reconcile-panel");
    const reconcileButton = byId("stock-journal-reconcile");
    const numberFormat = new Intl.NumberFormat("pt-BR");
    const currencyFormat = new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" });
    const dateFormat = new Intl.DateTimeFormat("pt-BR", { dateStyle: "short", timeStyle: "short" });
    const labels = { entrada: "Entrada", saida: "Saída", ajuste: "Ajuste", transferencia: "Transferência", estorno: "Estorno / correção" };
    let token = null;
    let busy = false;
    let canReconcile = false;
    let currentPage = 1;
    let lastPage = 1;

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        logoutButton.disabled = value;
        searchButton.disabled = value;
        previousButton.disabled = value || currentPage <= 1;
        nextButton.disabled = value || currentPage >= lastPage;
        reconcileButton.disabled = value || !canReconcile;
        for (const control of filters.querySelectorAll("input, select")) control.disabled = value;
    }

    async function readResponse(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const error = new Error(data.error?.message || data.message || data.detail || data.title || `Falha na requisição (${response.status}).`);
            error.code = data.error?.code || data.code || data.title;
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

    function filterUrl() {
        const params = new URLSearchParams();
        const type = byId("stock-journal-type").value;
        const q = byId("stock-journal-query").value.trim();
        const from = byId("stock-journal-from").value;
        const to = byId("stock-journal-to").value;
        if (type) params.set("type", type);
        if (q) params.set("q", q);
        if (from) params.set("from", from);
        if (to) params.set("to", to);
        params.set("page", String(currentPage));
        params.set("pageSize", "25");
        return `/api/v1/stock/movements?${params.toString()}`;
    }

    function appendCell(row, text, className = "") {
        const cell = document.createElement("td");
        if (className) cell.className = className;
        cell.textContent = text;
        row.append(cell);
    }

    async function downloadAttachment(path, button) {
        button.disabled = true;
        try {
            const response = await fetch(path, { cache: "no-store", headers: { Authorization: `Bearer ${token}` } });
            if (!response.ok) throw new Error(`Falha ao baixar a nota (${response.status}).`);
            const url = URL.createObjectURL(await response.blob());
            const link = document.createElement("a");
            link.href = url;
            link.download = "nota-estoque";
            link.click();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        } catch (error) {
            setMessage(error.message || "Não foi possível baixar a nota.", true);
        } finally {
            button.disabled = false;
        }
    }

    function renderMovement(movement) {
        const row = document.createElement("tr");
        const date = new Date(movement.created_at || movement.createdAt);
        appendCell(row, Number.isNaN(date.valueOf()) ? "—" : dateFormat.format(date), "text-nowrap small");
        appendCell(row, movement.type_label || labels[movement.type] || movement.type || "—");

        const itemCell = document.createElement("td");
        const item = movement.item || {};
        const itemName = document.createElement("div");
        itemName.className = "fw-semibold";
        itemName.textContent = item.name || "Brinde";
        const itemCode = document.createElement("div");
        itemCode.className = "small text-secondary";
        itemCode.textContent = item.code || String(item.id || movement.itemId || "");
        itemCell.append(itemName, itemCode);
        row.append(itemCell);

        const quantity = Number(movement.qty ?? movement.quantity ?? 0);
        appendCell(row, `${quantity > 0 ? "+" : ""}${numberFormat.format(quantity)}`, `text-end ${quantity >= 0 ? "text-success" : "text-danger"}`);
        appendCell(row, numberFormat.format(movement.balance_after ?? movement.balanceAfter ?? 0), "text-end");
        appendCell(row, movement.user?.name || "—");
        appendCell(row, movement.purpose || movement.reason || movement.recipient || movement.document_ref || movement.documentRef || movement.notes || "—");
        const documentCell = document.createElement("td");
        const unitValue = movement.unit_value ?? movement.unitValue;
        if (unitValue !== null && unitValue !== undefined && Number.isFinite(Number(unitValue)))
            documentCell.append(document.createTextNode(currencyFormat.format(Number(unitValue))));
        if (movement.hasAttachment) {
            const button = document.createElement("button");
            button.type = "button";
            button.className = "btn btn-sm btn-outline-primary";
            button.textContent = "Baixar NF";
            button.setAttribute("aria-label", `Baixar nota da movimentação ${movement.id}`);
            button.addEventListener("click", () => downloadAttachment(`/api/v1/stock/movements/${encodeURIComponent(movement.id)}/attachment`, button));
            documentCell.append(document.createTextNode(documentCell.textContent ? " " : ""), button);
        }
        if (!documentCell.childNodes.length) documentCell.textContent = "—";
        row.append(documentCell);
        return row;
    }

    async function load(allowWhileBusy = false) {
        if ((busy && !allowWhileBusy) || !token) return;
        const from = byId("stock-journal-from").value;
        const to = byId("stock-journal-to").value;
        if (from && to && from > to) {
            setMessage("A data inicial não pode ser posterior à data final.", true);
            return;
        }
        setBusy(true);
        setMessage("Carregando movimentações…");
        try {
            const response = await api(filterUrl());
            const items = Array.isArray(response.items) ? response.items : [];
            const pageSize = Math.max(1, Number(response.pageSize) || 25);
            const total = Math.max(0, Number(response.total) || 0);
            currentPage = Math.max(1, Number(response.page) || currentPage);
            lastPage = Math.max(1, Math.ceil(total / pageSize));
            rowsBody.replaceChildren();
            if (items.length) {
                for (const item of items) rowsBody.append(renderMovement(item));
                setMessage(`${numberFormat.format(total)} movimentação(ões) encontrada(s).`);
            } else {
                const row = document.createElement("tr");
                const cell = document.createElement("td");
                cell.colSpan = 8;
                cell.className = "text-center py-4 text-secondary";
                cell.textContent = "Nenhuma movimentação encontrada para os filtros selecionados.";
                row.append(cell);
                rowsBody.append(row);
                setMessage("Nenhuma movimentação encontrada.");
            }
            pageLabel.textContent = `Página ${currentPage} de ${lastPage} (${numberFormat.format(total)} registros)`;
            return true;
        } catch (error) {
            rowsBody.replaceChildren();
            setMessage(error.message || "Não foi possível carregar as movimentações.", true);
            return false;
        } finally {
            setBusy(false);
        }
    }

    function clearSession() {
        token = null;
        canReconcile = false;
        currentPage = 1;
        lastPage = 1;
        loginForm.reset();
        filters.reset();
        rowsBody.replaceChildren();
        pageLabel.textContent = "";
        results.classList.add("d-none");
        reconcilePanel.classList.add("d-none");
        sessionPanel.classList.add("d-none");
        loginForm.classList.remove("d-none");
        setBusy(false);
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
            token = data.accessToken || null;
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de consultar o estoque.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            if (!Array.isArray(data.permissions) || !data.permissions.includes("stock.view"))
                throw new Error("Esta tela exige a permissão stock.view.");
            canReconcile = data.permissions.includes("stock.adjust");
            reconcilePanel.classList.toggle("d-none", !canReconcile);
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            results.classList.remove("d-none");
            await load(true);
        } catch (error) {
            if (token) {
                await fetch("/api/v1/auth/logout", {
                    method: "POST", cache: "no-store", headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
                }).catch(() => {});
            }
            clearSession();
            setMessage(error.message || "Não foi possível entrar.", true);
        } finally {
            setBusy(false);
        }
    });

    reconcileButton.addEventListener("click", async () => {
        if (busy || !token || !canReconcile) return;
        if (!window.confirm("A conciliação criará somente posições ou movimentos de saldo inicial ausentes a partir do saldo atual. Continuar?")) return;
        setBusy(true);
        setMessage("Conciliando posições e ledger do estoque…");
        try {
            const response = await api("/api/v1/stock/reconcile", { method: "POST" });
            currentPage = 1;
            const refreshed = await load(true);
            if (refreshed) {
                const data = response.data || {};
                setMessage(`Conciliação concluída: ${numberFormat.format(data.total_items || 0)} item(ns), ${numberFormat.format(data.fixed_positions || 0)} posição(ões) e ${numberFormat.format(data.fixed_movements || 0)} movimento(s) corrigido(s).`);
            }
        } catch (error) {
            setMessage(error.message || "Não foi possível conciliar o estoque.", true);
        } finally {
            setBusy(false);
        }
    });

    filters.addEventListener("submit", event => {
        event.preventDefault();
        if (busy || !token) return;
        currentPage = 1;
        load();
    });

    previousButton.addEventListener("click", () => {
        if (busy || currentPage <= 1) return;
        currentPage--;
        load();
    });

    nextButton.addEventListener("click", () => {
        if (busy || currentPage >= lastPage) return;
        currentPage++;
        load();
    });

    logoutButton.addEventListener("click", async () => {
        if (busy || !token) return;
        setBusy(true);
        try {
            await fetch("/api/v1/auth/logout", {
                method: "POST", cache: "no-store", headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
            });
        } finally {
            clearSession();
            setMessage("Sessão encerrada.");
        }
    });
})();
