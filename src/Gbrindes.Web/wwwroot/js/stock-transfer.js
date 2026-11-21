(() => {
    const page = document.getElementById("stock-transfer-page");
    if (!page) return;

    const loginForm = document.getElementById("transfer-login-form");
    const emailInput = document.getElementById("transfer-email");
    const passwordInput = document.getElementById("transfer-password");
    const loginButton = document.getElementById("transfer-login");
    const sessionPanel = document.getElementById("transfer-session");
    const userLabel = document.getElementById("transfer-user-label");
    const logoutButton = document.getElementById("transfer-logout");
    const formPanel = document.getElementById("transfer-form-panel");
    const transferForm = document.getElementById("stock-transfer-form");
    const fields = document.getElementById("transfer-fields");
    const industrySelect = document.getElementById("transfer-industry");
    const itemSelect = document.getElementById("transfer-item");
    const fromSelect = document.getElementById("transfer-from");
    const toSelect = document.getElementById("transfer-to");
    const itemSearchForm = document.getElementById("item-search-form");
    const itemSearch = document.getElementById("item-search");
    const searchButton = document.getElementById("item-search-button");
    const locationCreateForm = document.getElementById("location-create-form");
    const newLocationName = document.getElementById("transfer-new-location");
    const createLocationButton = document.getElementById("transfer-create-location");
    const locationCreateHint = document.getElementById("location-create-hint");
    const submitButton = document.getElementById("transfer-submit");
    const message = document.getElementById("transfer-message");
    const resultPanel = document.getElementById("transfer-result");
    let token = null;
    let busy = false;
    let pendingFingerprint = null;
    let pendingKey = null;
    let locationRows = [];

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
        newLocationName.disabled = value;
        createLocationButton.disabled = value;
    }

    async function responseData(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const fieldMessages = data.errors && Object.values(data.errors).flat().join(" ");
            const error = new Error(fieldMessages || data.message || data.title || data.error?.message || data.detail || `Falha na requisição (${response.status}).`);
            error.status = response.status;
            throw error;
        }
        return data;
    }

    async function api(path, init = {}) {
        const response = await fetch(path, {
            cache: "no-store",
            ...init,
            headers: {
                Authorization: `Bearer ${token}`,
                Accept: "application/json",
                ...(init.headers || {})
            }
        });
        return responseData(response);
    }

    function setOptions(select, rows, placeholder, label) {
        const current = select.value;
        select.replaceChildren(new Option(placeholder, ""));
        for (const row of rows) {
            const option = new Option(label(row), String(row.id));
            select.add(option);
        }
        if (rows.some(row => String(row.id) === current)) select.value = current;
    }

    async function fetchIndustries() {
        const first = await api("/api/v1/industries?page=1&per_page=100");
        const rows = Array.isArray(first.items) ? [...first.items] : [];
        const pageSize = Math.max(1, Number(first.per_page) || 100);
        const totalPages = Math.ceil((Number(first.total) || rows.length) / pageSize);
        for (let pageNumber = 2; pageNumber <= totalPages; pageNumber++) {
            const pageResult = await api(`/api/v1/industries?page=${pageNumber}&per_page=${pageSize}`);
            rows.push(...(Array.isArray(pageResult.items) ? pageResult.items : []));
        }
        return rows;
    }

    async function loadItems(query = "") {
        const suffix = query.trim() ? `?q=${encodeURIComponent(query.trim())}` : "";
        const response = await api(`/api/v1/items/options${suffix}`);
        const rows = Array.isArray(response.data) ? response.data : [];
        setOptions(itemSelect, rows, rows.length ? "Selecione um brinde" : "Nenhum brinde encontrado", row => `${row.code} — ${row.name}`);
        if (!rows.some(row => String(row.id) === itemSelect.value)) itemSelect.value = "";
        return rows.length;
    }

    async function loadOptions() {
        const [industries, locations] = await Promise.all([
            fetchIndustries(),
            api("/api/v1/locations?all=1")
        ]);
        setOptions(industrySelect, industries, "Selecione uma indústria", row => row.name);
        locationRows = Array.isArray(locations) ? locations : [];
        setOptions(fromSelect, locationRows, "Padrão da API", row => row.name);
        setOptions(toSelect, locationRows, "Selecione um local", row => row.name);
        await loadItems();
        formPanel.classList.remove("d-none");
        setMessage("Cadastros carregados. A API continuará validando acesso, escopo e saldo por posição.");
    }

    locationCreateForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token || !locationCreateForm.reportValidity()) return;
        const name = newLocationName.value.trim();
        if (!name) return;
        setBusy(true);
        setMessage("Cadastrando local…");
        try {
            const created = await api("/api/v1/locations", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ name, kind: "outro", description: "Local de destino da transferência", active: true })
            });
            const row = created?.data || created;
            if (!row?.id || !row?.name) throw new Error("A API não confirmou o novo local.");
            locationRows = [...locationRows, row].sort((left, right) =>
                left.name.localeCompare(right.name, "pt-BR") || String(left.id).localeCompare(String(right.id)));
            setOptions(fromSelect, locationRows, "Padrão da API", location => location.name);
            setOptions(toSelect, locationRows, "Selecione um local", location => location.name);
            toSelect.value = String(row.id);
            locationCreateForm.reset();
            setMessage(`Local “${row.name}” cadastrado e selecionado como destino.`);
        } catch (error) {
            setMessage(error.message || "Não foi possível cadastrar o local.", true);
        } finally {
            setBusy(false);
        }
    });

    function newIdempotencyKey() {
        return globalThis.crypto?.randomUUID
            ? globalThis.crypto.randomUUID()
            : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
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
            if (data.mustChangePassword)
                throw new Error("Esta conta precisa trocar a senha antes de transferir estoque.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            const permissions = Array.isArray(data.permissions) ? data.permissions : [];
            const missing = ["stock.transfer", "items.view", "lookups.view"].filter(permission => !permissions.includes(permission));
            if (missing.length) throw new Error(`Permissões necessárias para esta tela: ${missing.join(", ")}.`);
            const canManageLocations = permissions.includes("lookups.manage");
            locationCreateForm.classList.toggle("d-none", !canManageLocations);
            locationCreateHint.classList.toggle("d-none", canManageLocations);
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            await loadOptions();
        } catch (error) {
            if (token) {
                await fetch("/api/v1/auth/logout", {
                    method: "POST",
                    cache: "no-store",
                    headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
                }).catch(() => {});
            }
            token = null;
            formPanel.classList.add("d-none");
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            setMessage(error.message || "Não foi possível entrar.", true);
        } finally {
            setBusy(false);
        }
    });

    itemSearchForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token) return;
        setBusy(true);
        setMessage("Buscando brindes…");
        try {
            const count = await loadItems(itemSearch.value);
            setMessage(count ? `${count} brinde(s) disponíveis nesta busca.` : "Nenhum brinde encontrado.");
        } catch (error) {
            setMessage(error.message || "Não foi possível buscar os brindes.", true);
        } finally {
            setBusy(false);
        }
    });

    transferForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token || !transferForm.reportValidity()) return;
        if (fromSelect.value && fromSelect.value === toSelect.value) {
            setMessage("Origem e destino precisam ser diferentes.", true);
            return;
        }
        const body = {
            item_id: itemSelect.value,
            quantity: Number(document.getElementById("transfer-quantity").value),
            industry_id: industrySelect.value,
            from_location_id: fromSelect.value || null,
            to_location_id: toSelect.value,
            notes: document.getElementById("transfer-notes").value.trim() || null
        };
        const fingerprint = JSON.stringify(body);
        const confirmed = window.confirm("Confirmar a transferência entre locais? Isso não é uma retirada; o saldo total não deve mudar. A API validará o saldo da origem.");
        if (!confirmed) return;
        if (fingerprint !== pendingFingerprint || !pendingKey) {
            pendingFingerprint = fingerprint;
            pendingKey = newIdempotencyKey();
        }
        setBusy(true);
        resultPanel.classList.add("d-none");
        setMessage("Registrando transferência…");
        try {
            const response = await api("/api/v1/stock/transfers", {
                method: "POST",
                headers: { "Content-Type": "application/json", "Idempotency-Key": pendingKey },
                body: JSON.stringify(body)
            });
            const data = response.data || {};
            const movementCount = Array.isArray(data.movements) ? data.movements.length : 0;
            resultPanel.textContent = `Transferência concluída: ${movementCount} movimentos registrados. Saldo total: ${data.stock?.on_hand ?? "não informado"}.`;
            resultPanel.classList.remove("d-none");
            pendingFingerprint = null;
            pendingKey = null;
            setMessage("Transferência registrada. As posições de origem/destino foram atualizadas pela API.");
        } catch (error) {
            const networkFailure = !error.status || error.status === 0;
            if (networkFailure && window.OfflineQueue?.canQueue("POST", "/api/v1/stock/transfers", body, pendingKey)) {
                try {
                    await window.OfflineQueue.enqueue({ path: "/api/v1/stock/transfers", body, idempotencyKey: pendingKey });
                    window.OfflineQueue.setAccessToken(token);
                    pendingFingerprint = null;
                    pendingKey = null;
                    transferForm.reset();
                    setMessage("Sem conexão. Transferência guardada neste dispositivo e será sincronizada quando a conexão voltar.");
                    return;
                } catch (queueError) {
                    setMessage(queueError.message || "Não foi possível guardar a transferência offline.", true);
                    return;
                }
            }
            setMessage(error.message || "Não foi possível registrar a transferência. Uma nova tentativa com os mesmos dados reutilizará a chave idempotente.", true);
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
            pendingFingerprint = null;
            pendingKey = null;
            transferForm.reset();
            locationCreateForm.reset();
            locationCreateForm.classList.add("d-none");
            locationCreateHint.classList.add("d-none");
            itemSearch.value = "";
            resultPanel.textContent = "";
            resultPanel.classList.add("d-none");
            formPanel.classList.add("d-none");
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });
})();
