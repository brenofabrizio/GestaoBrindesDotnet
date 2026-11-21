(() => {
    const page = document.getElementById("item-detail-page");
    if (!page) return;

    const itemId = page.dataset.itemId;
    const loginForm = document.getElementById("item-login-form");
    const emailInput = document.getElementById("item-email");
    const passwordInput = document.getElementById("item-password");
    const loginButton = document.getElementById("item-login");
    const sessionPanel = document.getElementById("item-session");
    const userLabel = document.getElementById("item-user-label");
    const logoutButton = document.getElementById("item-logout");
    const content = document.getElementById("item-detail-content");
    const message = document.getElementById("item-message");
    const historyList = document.getElementById("item-history");
    const historyMore = document.getElementById("item-history-more");
    const stockSection = document.getElementById("item-stock-section");
    const positions = document.getElementById("item-stock-positions");
    const entrySection = document.getElementById("stock-entry-section");
    const entryForm = document.getElementById("stock-entry-form");
    const entryFields = document.getElementById("stock-entry-fields");
    const entryMessage = document.getElementById("stock-entry-message");
    const photo = document.getElementById("item-photo");
    let token = null;
    let pageNumber = 1;
    let lastPage = 1;
    let busy = false;
    let photoObjectUrl = null;
    let pendingEntryKey = null;
    let pendingEntryPayload = null;
    let canViewStock = false;
    const numberFormat = new Intl.NumberFormat("pt-BR");
    const moneyFormat = new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" });
    const dateFormat = new Intl.DateTimeFormat("pt-BR", { dateStyle: "short", timeStyle: "short" });

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        logoutButton.disabled = value;
        historyMore.disabled = value || pageNumber > lastPage;
        entryFields.disabled = value;
    }

    async function responseData(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            throw new Error(data.detail || data.message || data.title || data.error || `Falha na requisição (${response.status}).`);
        }
        return data;
    }

    async function api(path) {
        const response = await fetch(path, {
            cache: "no-store",
            headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
        });
        return responseData(response);
    }

    function addHistoryEntry(entry) {
        const li = document.createElement("li");
        li.className = "list-group-item px-0";
        const meta = document.createElement("div");
        meta.className = "small text-secondary";
        const timestamp = entry.at ? dateFormat.format(new Date(entry.at)) : "Data não informada";
        meta.textContent = `${timestamp} · ${entry.user?.name || "Usuário não identificado"}`;
        const title = document.createElement("div");
        title.className = "fw-semibold";
        title.textContent = entry.action_label || entry.action || "Alteração";
        li.append(meta, title);
        if (entry.quantity !== null && entry.quantity !== undefined) {
            const quantity = document.createElement("div");
            quantity.textContent = `Quantidade: ${numberFormat.format(entry.quantity)} · Saldo após: ${numberFormat.format(entry.balance_after ?? 0)}`;
            li.append(quantity);
        }
        const details = [entry.reason, entry.purpose, entry.recipient, entry.purchase_ticket_no, entry.notes].filter(Boolean);
        if (details.length) {
            const note = document.createElement("div");
            note.className = "small text-secondary";
            note.textContent = details.join(" · ");
            li.append(note);
        }
        historyList.append(li);
    }

    function renderPositions(rows) {
        positions.replaceChildren();
        if (!rows.length) {
            const empty = document.createElement("p");
            empty.className = "text-secondary mb-0";
            empty.textContent = "Nenhuma posição com saldo positivo.";
            positions.append(empty);
            return;
        }
        const table = document.createElement("table");
        table.className = "table table-sm align-middle mb-0";
        const headRow = document.createElement("tr");
        for (const label of ["Local", "Tipo", "Quantidade"]) {
            const th = document.createElement("th");
            th.scope = "col";
            th.textContent = label;
            headRow.append(th);
        }
        const head = document.createElement("thead");
        head.className = "table-light";
        head.append(headRow);
        const body = document.createElement("tbody");
        for (const row of rows) {
            const tr = document.createElement("tr");
            for (const value of [row.location?.name, row.location?.kind, numberFormat.format(row.qty ?? 0)]) {
                const td = document.createElement("td");
                td.textContent = value ?? "—";
                tr.append(td);
            }
            body.append(tr);
        }
        table.append(head, body);
        positions.append(table);
    }

    async function loadPositions() {
        const result = await api(`/api/v1/stock/positions?item_id=${encodeURIComponent(itemId)}`);
        renderPositions(Array.isArray(result.data) ? result.data : []);
    }

    async function loadPhoto(photoPath) {
        if (typeof photoPath !== "string") return;
        const url = new URL(photoPath, window.location.origin);
        if (url.origin !== window.location.origin || !url.pathname.startsWith("/api/v1/items/")) return;
        try {
            const response = await fetch(url, {
                cache: "no-store",
                headers: { Authorization: `Bearer ${token}` }
            });
            if (!response.ok) return;
            const blob = await response.blob();
            if (!token) return;
            photoObjectUrl = URL.createObjectURL(blob);
            photo.src = photoObjectUrl;
            photo.classList.remove("d-none");
        } catch {
            // A photo is optional; keep the rest of the item details available.
        }
    }

    async function loadHistory(reset = false) {
        if (!token || busy) return;
        if (reset) {
            pageNumber = 1;
            lastPage = 1;
            historyList.replaceChildren();
        }
        setBusy(true);
        try {
            const result = await api(`/api/v1/items/${encodeURIComponent(itemId)}/history?page=${pageNumber}&per_page=20`);
            for (const entry of Array.isArray(result.data) ? result.data : []) addHistoryEntry(entry);
            lastPage = result.meta?.last_page || 1;
            pageNumber++;
            historyMore.hidden = pageNumber > lastPage;
            setMessage("Detalhes e histórico atualizados.");
        } catch (error) {
            setMessage(error.message || "Não foi possível carregar o histórico.", true);
        } finally {
            setBusy(false);
        }
    }

    async function loadDetails(permissions) {
        setBusy(true);
        setMessage("Carregando detalhes do brinde…");
        try {
            canViewStock = permissions.includes("stock.view");
            const item = await api(`/api/v1/items/${encodeURIComponent(itemId)}`);
            document.getElementById("item-code").textContent = item.code || "";
            document.getElementById("item-name").textContent = item.name || "Brinde sem nome";
            document.getElementById("item-status").textContent = item.status === "ativo" ? "Ativo" : item.status === "inativo" ? "Inativo" : item.status || "Status não informado";
            document.getElementById("item-description").textContent = item.description || "Sem descrição.";
            document.getElementById("item-category").textContent = item.category?.name || "—";
            document.getElementById("item-industry").textContent = item.industry?.name || "—";
            document.getElementById("item-location").textContent = item.location?.name || "—";
            document.getElementById("item-supplier").textContent = item.supplier?.name || "—";
            document.getElementById("item-min-stock").textContent = numberFormat.format(item.minStock ?? 0);
            document.getElementById("item-unit-value").textContent = item.unitValue == null ? "—" : moneyFormat.format(item.unitValue);
            content.classList.remove("d-none");
            const photoPath = item.thumb_url || item.photo_url;
            if (photoPath) void loadPhoto(photoPath);
            if (canViewStock) {
                stockSection.classList.remove("d-none");
                try {
                    await loadPositions();
                } catch (error) {
                    positions.textContent = error.message || "Não foi possível consultar as posições.";
                }
            }
            if (permissions.includes("stock.entry")) entrySection.classList.remove("d-none");
            pageNumber = 1;
            lastPage = 1;
            setBusy(false);
            await loadHistory(true);
        } catch (error) {
            setMessage(error.message || "Não foi possível carregar os detalhes do brinde.", true);
        } finally {
            setBusy(false);
        }
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
            const data = await responseData(response);
            passwordInput.value = "";
            token = data.accessToken || null;
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de consultar o catálogo.");
            if (!Array.isArray(data.permissions) || !data.permissions.includes("items.view"))
                throw new Error("A conta não possui permissão para consultar este brinde.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            setBusy(false);
            await loadDetails(data.permissions);
        } catch (error) {
            if (token) {
                await fetch("/api/v1/auth/logout", {
                    method: "POST",
                    cache: "no-store",
                    headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
                }).catch(() => {});
            }
            token = null;
            setMessage(error.message || "Não foi possível entrar.", true);
        } finally {
            setBusy(false);
        }
    });

    entryForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token || !entryForm.reportValidity()) return;
        const values = Object.fromEntries(new FormData(entryForm).entries());
        const payload = {
            quantity: Number(values.quantity),
            reason: values.reason || null,
            document_ref: values.document_ref || null,
            purpose: values.purpose || null,
            purchase_ticket_no: values.purchase_ticket_no || null,
            recipient: values.recipient || null,
            notes: values.notes || null
        };
        const serialized = JSON.stringify(payload);
        if (serialized !== pendingEntryPayload || !pendingEntryKey) {
            pendingEntryPayload = serialized;
            pendingEntryKey = crypto.randomUUID();
        }
        setBusy(true);
        entryMessage.textContent = "Registrando entrada…";
        entryMessage.classList.remove("text-danger", "text-success");
        try {
            const response = await fetch(`/api/v1/items/${encodeURIComponent(itemId)}/entry`, {
                method: "POST",
                cache: "no-store",
                headers: {
                    Authorization: `Bearer ${token}`,
                    "Content-Type": "application/json",
                    Accept: "application/json"
                },
                body: JSON.stringify({ ...payload, idempotency_key: pendingEntryKey })
            });
            await responseData(response);
            entryForm.reset();
            pendingEntryKey = null;
            pendingEntryPayload = null;
            entryMessage.textContent = "Entrada registrada. O saldo e o histórico foram atualizados.";
            entryMessage.classList.add("text-success");
            setBusy(false);
            const refreshes = [loadHistory(true)];
            if (canViewStock)
                refreshes.push(loadPositions().catch(error => { positions.textContent = error.message || "Não foi possível atualizar as posições."; }));
            await Promise.all(refreshes);
        } catch (error) {
            entryMessage.textContent = error.message || "Não foi possível registrar a entrada. Reenvie sem alterar os dados para reutilizar a mesma chave.";
            entryMessage.classList.add("text-danger");
        } finally {
            setBusy(false);
        }
    });

    logoutButton.addEventListener("click", async () => {
        if (busy || !token) return;
        setBusy(true);
        try {
            await fetch("/api/v1/auth/logout", {
                method: "POST",
                cache: "no-store",
                headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
            });
        } finally {
            token = null;
            if (photoObjectUrl) URL.revokeObjectURL(photoObjectUrl);
            photoObjectUrl = null;
            photo.removeAttribute("src");
            photo.classList.add("d-none");
            historyList.replaceChildren();
            positions.replaceChildren();
            entryForm.reset();
            pendingEntryKey = null;
            pendingEntryPayload = null;
            canViewStock = false;
            entryMessage.textContent = "";
            content.classList.add("d-none");
            stockSection.classList.add("d-none");
            entrySection.classList.add("d-none");
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            historyMore.hidden = false;
            pageNumber = 1;
            lastPage = 1;
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });

    historyMore.addEventListener("click", () => void loadHistory());
})();
