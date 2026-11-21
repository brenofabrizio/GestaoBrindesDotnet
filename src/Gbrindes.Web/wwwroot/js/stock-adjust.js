(() => {
    const byId = id => document.getElementById(id);
    const loginForm = byId("stock-adjust-login-form");
    if (!loginForm) return;

    const emailInput = byId("stock-adjust-email");
    const passwordInput = byId("stock-adjust-password");
    const loginButton = byId("stock-adjust-login");
    const sessionPanel = byId("stock-adjust-session");
    const userLabel = byId("stock-adjust-user-label");
    const logoutButton = byId("stock-adjust-logout");
    const formPanel = byId("stock-adjust-form-panel");
    const searchForm = byId("stock-adjust-search");
    const queryInput = byId("stock-adjust-query");
    const searchButton = byId("stock-adjust-search-button");
    const form = byId("stock-adjust-form");
    const fields = byId("stock-adjust-fields");
    const itemSelect = byId("stock-adjust-item");
    const modeSelect = byId("stock-adjust-mode");
    const quantityInput = byId("stock-adjust-quantity");
    const quantityHelp = byId("stock-adjust-quantity-help");
    const message = byId("stock-adjust-message");
    const resultPanel = byId("stock-adjust-result");
    const numberFormat = new Intl.NumberFormat("pt-BR");
    let token = null;
    let busy = false;
    let pendingFingerprint = null;
    let pendingKey = null;
    let itemsById = new Map();

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
        fields.disabled = value;
    }

    async function readResponse(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const detail = data.error?.message || data.message || data.detail || data.title || `Falha na requisição (${response.status}).`;
            const error = new Error(detail);
            error.status = response.status;
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

    function updateQuantityMode() {
        const delta = modeSelect.value === "delta";
        quantityInput.min = delta ? "-1000000" : "0";
        quantityInput.value = delta && quantityInput.value === "0" ? "1" : quantityInput.value;
        quantityHelp.textContent = delta
            ? "Valor positivo soma; valor negativo subtrai. Zero não é permitido."
            : "Informe a contagem física total, que pode ser zero.";
        validateQuantity();
    }

    function validateQuantity() {
        const quantity = Number(quantityInput.value);
        const invalidDelta = modeSelect.value === "delta" && quantity === 0;
        quantityInput.setCustomValidity(invalidDelta ? "Informe uma quantidade diferente de zero para somar ou subtrair." : "");
    }

    function setItemOptions(rows) {
        const current = itemSelect.value;
        itemSelect.replaceChildren(new Option(rows.length ? "Selecione um brinde" : "Nenhum brinde encontrado", ""));
        itemsById = new Map(rows.map(item => [String(item.id), item]));
        for (const item of rows) {
            itemSelect.add(new Option(`${item.code} — ${item.name} · saldo total ${numberFormat.format(item.available || 0)}`, String(item.id)));
        }
        itemSelect.value = itemsById.has(current) ? current : "";
        updateItemSummary();
    }

    function updateItemSummary() {
        const item = itemsById.get(itemSelect.value);
        byId("stock-adjust-item-summary").textContent = item
            ? `${item.code} — ${item.name}. Saldo total atual: ${numberFormat.format(item.available || 0)}.`
            : "A busca usa o catálogo ativo; o saldo exibido é o saldo total, não o saldo por local.";
    }

    async function loadItems(query = "") {
        const suffix = query.trim() ? `?q=${encodeURIComponent(query.trim())}` : "";
        const response = await api(`/api/v1/items/options${suffix}`);
        setItemOptions(Array.isArray(response.data) ? response.data : []);
    }

    function createKey() {
        return globalThis.crypto?.randomUUID
            ? globalThis.crypto.randomUUID()
            : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
    }

    function clearSession() {
        token = null;
        pendingFingerprint = null;
        pendingKey = null;
        itemsById.clear();
        form.reset();
        queryInput.value = "";
        setItemOptions([]);
        resultPanel.replaceChildren();
        resultPanel.classList.add("d-none");
        formPanel.classList.add("d-none");
        sessionPanel.classList.add("d-none");
        loginForm.classList.remove("d-none");
        passwordInput.value = "";
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
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de ajustar estoque.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            const permissions = Array.isArray(data.permissions) ? data.permissions : [];
            const missing = ["stock.adjust", "items.view"].filter(permission => !permissions.includes(permission));
            if (missing.length) throw new Error(`Permissões necessárias para esta tela: ${missing.join(", ")}.`);
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            try {
                await loadItems();
                setMessage("Catálogo carregado. Escolha o tipo de ajuste e confirme os dados antes de registrar.");
            } catch (error) {
                setMessage(`Não foi possível carregar os brindes: ${error.message}`, true);
            }
            formPanel.classList.remove("d-none");
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

    searchForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token) return;
        setBusy(true);
        setMessage("Buscando brindes…");
        try {
            await loadItems(queryInput.value);
            setMessage(itemsById.size ? `${itemsById.size} brinde(s) encontrados.` : "Nenhum brinde encontrado.");
        } catch (error) {
            setMessage(error.message || "Não foi possível buscar os brindes.", true);
        } finally {
            setBusy(false);
        }
    });

    modeSelect.addEventListener("change", updateQuantityMode);
    quantityInput.addEventListener("input", validateQuantity);
    itemSelect.addEventListener("change", updateItemSummary);

    form.addEventListener("submit", async event => {
        event.preventDefault();
        validateQuantity();
        if (busy || !token || !form.reportValidity()) return;
        const item = itemsById.get(itemSelect.value);
        if (!item) {
            setMessage("Selecione um brinde válido da busca.", true);
            return;
        }
        const body = {
            item_id: item.id,
            mode: modeSelect.value,
            quantity: Number(quantityInput.value),
            reason: byId("stock-adjust-reason").value.trim(),
            notes: byId("stock-adjust-notes").value.trim() || null
        };
        const fingerprint = JSON.stringify(body);
        const amountText = body.mode === "delta"
            ? `${body.quantity > 0 ? "+" : ""}${body.quantity} unidade(s)`
            : `saldo final de ${body.quantity} unidade(s)`;
        if (!window.confirm(`Registrar ajuste de ${item.code} — ${item.name}: ${amountText}? O histórico registrará o motivo informado.`)) return;
        if (pendingFingerprint !== fingerprint || !pendingKey) {
            pendingFingerprint = fingerprint;
            pendingKey = createKey();
        }
        setBusy(true);
        resultPanel.replaceChildren();
        resultPanel.classList.add("d-none");
        setMessage("Registrando ajuste…");
        try {
            const response = await api("/api/v1/stock/adjustments", {
                method: "POST",
                headers: { "Content-Type": "application/json", "Idempotency-Key": pendingKey },
                body: JSON.stringify(body)
            });
            const movement = response.data || {};
            pendingFingerprint = null;
            pendingKey = null;
            resultPanel.replaceChildren();
            const heading = document.createElement("strong");
            heading.textContent = "Ajuste registrado";
            const detail = document.createElement("p");
            detail.className = "mb-2";
            detail.textContent = `Saldo total após o ajuste: ${numberFormat.format(movement.balanceAfter ?? 0)}. Movimento: ${movement.type || "ajuste"} (${movement.quantity > 0 ? "+" : ""}${movement.quantity ?? body.quantity}).`;
            resultPanel.append(heading, detail);
            const newButton = document.createElement("button");
            newButton.type = "button";
            newButton.className = "btn btn-sm btn-outline-success";
            newButton.textContent = "Novo ajuste";
            newButton.addEventListener("click", () => {
                form.reset();
                modeSelect.value = "set";
                quantityInput.value = "0";
                updateQuantityMode();
                updateItemSummary();
                pendingFingerprint = null;
                pendingKey = null;
                resultPanel.replaceChildren();
                resultPanel.classList.add("d-none");
                setMessage("Informe os dados do próximo ajuste.");
            });
            resultPanel.append(newButton);
            resultPanel.classList.remove("d-none");
            setMessage("Ajuste registrado; saldo e histórico foram atualizados pela API.");
            form.reset();
            queryInput.value = "";
            itemSelect.value = "";
            updateItemSummary();
        } catch (error) {
            const networkFailure = !error.status || error.status === 0;
            if (networkFailure && window.OfflineQueue?.canQueue("POST", "/api/v1/stock/adjustments", body, pendingKey)) {
                try {
                    await window.OfflineQueue.enqueue({ path: "/api/v1/stock/adjustments", body, idempotencyKey: pendingKey });
                    window.OfflineQueue.setAccessToken(token);
                    pendingFingerprint = null;
                    pendingKey = null;
                    form.reset();
                    itemSelect.value = "";
                    updateItemSummary();
                    setMessage("Sem conexão. Ajuste guardado neste dispositivo e será sincronizado quando a conexão voltar.");
                    return;
                } catch (queueError) {
                    setMessage(queueError.message || "Não foi possível guardar o ajuste offline.", true);
                    return;
                }
            }
            setMessage(error.message || "Não foi possível registrar o ajuste. Um retry sem alteração reutilizará a mesma Idempotency-Key.", true);
        } finally {
            setBusy(false);
        }
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

    updateQuantityMode();
})();
