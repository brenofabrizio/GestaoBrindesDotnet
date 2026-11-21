(() => {
    const byId = id => document.getElementById(id);
    const loginForm = byId("stock-entry-page-login");
    if (!loginForm) return;

    const emailInput = byId("stock-entry-page-email");
    const passwordInput = byId("stock-entry-page-password");
    const loginButton = byId("stock-entry-page-login-button");
    const logoutButton = byId("stock-entry-page-logout");
    const searchForm = byId("stock-entry-page-search");
    const queryInput = byId("stock-entry-page-query");
    const searchButton = byId("stock-entry-page-search-button");
    const form = byId("stock-entry-page-form");
    const fields = byId("stock-entry-page-fields");
    const itemSelect = byId("stock-entry-page-item");
    const itemSummary = byId("stock-entry-page-item-summary");
    const industryWrap = byId("stock-entry-page-industry-wrap");
    const industrySelect = byId("stock-entry-page-industry");
    const workflow = byId("stock-entry-page-workflow");
    const sessionPanel = byId("stock-entry-page-session");
    const userLabel = byId("stock-entry-page-user");
    const message = byId("stock-entry-page-message");
    const resultPanel = byId("stock-entry-page-result");
    const numberFormat = new Intl.NumberFormat("pt-BR");
    let token = null;
    let canViewStock = false;
    let busy = false;
    let pendingFingerprint = null;
    let pendingKey = null;
    let items = new Map();
    let searchTimer = null;

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
            const error = new Error(data.error?.message || data.message || data.detail || data.title || `Falha na requisição (${response.status}).`);
            error.status = response.status;
            error.code = data.error?.code || data.code || data.title;
            throw error;
        }
        return data;
    }

    async function attachmentFingerprint(file) {
        if (!globalThis.crypto?.subtle) throw new Error("Este navegador não permite validar o arquivo com segurança.");
        const digest = await globalThis.crypto.subtle.digest("SHA-256", await file.arrayBuffer());
        return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
    }

    async function filePayload(file) {
        const bytes = new Uint8Array(await file.arrayBuffer());
        let binary = "";
        for (let offset = 0; offset < bytes.length; offset += 0x8000)
            binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
        return { field: "attachment", name: file.name, type: file.type || "application/octet-stream", dataBase64: btoa(binary) };
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

    async function api(path, init = {}) {
        const response = await fetch(path, {
            cache: "no-store",
            ...init,
            headers: { Authorization: `Bearer ${token}`, Accept: "application/json", ...(init.headers || {}) }
        });
        return readResponse(response);
    }

    function updateItemSummary() {
        const item = items.get(itemSelect.value);
        itemSummary.textContent = item
            ? `${item.code} — ${item.name}. Saldo atual: ${numberFormat.format(item.available || 0)} unidades.`
            : "O saldo exibido é informativo; o servidor valida e atualiza o saldo.";
    }

    function setItems(rows) {
        const selectedId = itemSelect.value;
        itemSelect.replaceChildren(new Option(rows.length ? "Selecione um brinde" : "Nenhum brinde encontrado", ""));
        items = new Map(rows.map(item => [String(item.id), item]));
        for (const item of rows) {
            itemSelect.add(new Option(`${item.code} — ${item.name} · saldo ${numberFormat.format(item.available || 0)}`, String(item.id)));
        }
        itemSelect.value = items.has(selectedId) ? selectedId : "";
        updateItemSummary();
    }

    async function loadItems(query = "") {
        const suffix = query.trim() ? `?q=${encodeURIComponent(query.trim())}` : "";
        const response = await api(`/api/v1/items/options${suffix}`);
        setItems(Array.isArray(response.data) ? response.data : []);
    }

    async function loadIndustries() {
        const response = await api("/api/v1/industries?active=1&per_page=100");
        industrySelect.replaceChildren(new Option("Sem indústria vinculada", ""));
        for (const industry of Array.isArray(response.items) ? response.items : [])
            industrySelect.add(new Option(industry.name, String(industry.id)));
    }

    function clearSession() {
        token = null;
        canViewStock = false;
        pendingFingerprint = null;
        pendingKey = null;
        items.clear();
        setItems([]);
        industrySelect.replaceChildren(new Option("Sem indústria vinculada", ""));
        industryWrap.classList.add("d-none");
        form.reset();
        queryInput.value = "";
        resultPanel.replaceChildren();
        resultPanel.classList.add("d-none");
        workflow.classList.add("d-none");
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
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de registrar entradas.");
            token = data.accessToken || null;
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            const permissions = Array.isArray(data.permissions) ? data.permissions : [];
            const missing = ["stock.entry", "items.view"].filter(permission => !permissions.includes(permission));
            if (missing.length) throw new Error(`Permissões necessárias para esta tela: ${missing.join(", ")}.`);
            canViewStock = permissions.includes("stock.view");
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            workflow.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            let industryNotice = "";
            if (permissions.includes("lookups.view")) {
                industryWrap.classList.remove("d-none");
                try {
                    await loadIndustries();
                } catch {
                    industryNotice = " A lista de indústrias não carregou; a entrada ficará sem indústria vinculada.";
                }
            }
            await loadItems();
            setMessage(`Catálogo carregado. Selecione o brinde e informe os dados do recebimento.${industryNotice}`);
        } catch (error) {
            if (token) {
                await fetch("/api/v1/auth/logout", {
                    method: "POST",
                    cache: "no-store",
                    headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
                }).catch(() => {});
            }
            token = null;
            canViewStock = false;
            workflow.classList.add("d-none");
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            setMessage(error.message || "Não foi possível autenticar ou carregar o catálogo.", true);
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
            setMessage("Busca concluída.");
        } catch (error) {
            setMessage(error.message || "Não foi possível buscar brindes.", true);
        } finally {
            setBusy(false);
        }
    });

    queryInput.addEventListener("input", () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => {
            if (queryInput.value.trim().length >= 2) searchForm.requestSubmit();
        }, 300);
    });

    itemSelect.addEventListener("change", updateItemSummary);

    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token || !form.reportValidity()) return;
        const values = Object.fromEntries(new FormData(form).entries());
        const itemId = values.item_id;
        if (!items.has(itemId)) {
            setMessage("Selecione um brinde ativo da lista.", true);
            return;
        }
        const payload = {
            quantity: Number(values.quantity),
            unit_value: values.unit_value.trim() === "" ? null : Number(values.unit_value),
            reason: values.reason.trim() || null,
            document_ref: values.document_ref.trim() || null,
            purpose: values.purpose.trim() || null,
            purchase_ticket_no: values.purchase_ticket_no.trim() || null,
            notes: values.notes.trim() || null
        };
        if (industrySelect.value) payload.industry_id = industrySelect.value;
        const attachment = values.attachment instanceof File && values.attachment.size > 0 ? values.attachment : null;
        setBusy(true);
        resultPanel.classList.add("d-none");
        setMessage("Preparando entrada…");
        try {
            if (attachment && attachment.size > 10 * 1024 * 1024) throw new Error("A nota fiscal deve ter até 10 MB.");
            const attachmentHash = attachment ? await attachmentFingerprint(attachment) : null;
            const fingerprint = JSON.stringify({ itemId, ...payload, attachmentHash });
            if (fingerprint !== pendingFingerprint || !pendingKey) {
                pendingFingerprint = fingerprint;
                pendingKey = globalThis.crypto?.randomUUID ? globalThis.crypto.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
            }
            setMessage("Registrando entrada…");
            let request;
            if (attachment) {
                const body = new FormData();
                for (const [name, value] of Object.entries({ ...payload, idempotency_key: pendingKey }))
                    if (value !== null) body.append(name, String(value));
                body.append("attachment", attachment, attachment.name);
                request = { method: "POST", body };
            } else {
                request = {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ ...payload, idempotency_key: pendingKey })
                };
            }
            const movement = await api(`/api/v1/items/${encodeURIComponent(itemId)}/entry`, request);
            pendingFingerprint = null;
            pendingKey = null;
            resultPanel.replaceChildren(document.createTextNode(`Entrada registrada. Quantidade: ${numberFormat.format(movement.quantity || payload.quantity)}. Saldo após o movimento: ${numberFormat.format(movement.balanceAfter ?? movement.balance_after ?? 0)}.`));
            if (movement.attachment_url && canViewStock) {
                const downloadButton = document.createElement("button");
                downloadButton.type = "button";
                downloadButton.className = "btn btn-sm btn-outline-primary ms-2";
                downloadButton.textContent = "Baixar nota anexada";
                downloadButton.addEventListener("click", () => downloadAttachment(movement.attachment_url, downloadButton));
                resultPanel.append(" ", downloadButton);
            } else if (movement.attachment_url) {
                resultPanel.append(" ", document.createTextNode("Nota armazenada em área privada; consulta exige stock.view."));
            }
            resultPanel.classList.remove("d-none");
            form.reset();
            itemSelect.value = "";
            updateItemSummary();
            setMessage("Movimentação registrada no ledger.");
        } catch (error) {
            const networkFailure = !error.status || error.status === 0;
            if (networkFailure && attachment && window.OfflineQueue?.canQueue("POST", `/api/v1/items/${encodeURIComponent(itemId)}/entry`, {
                __multipart: true,
                fields: { ...payload, idempotency_key: pendingKey },
                file: { field: "attachment", name: attachment.name, type: attachment.type || "application/octet-stream", dataBase64: "pending" }
            }, pendingKey)) {
                try {
                    const offlineBody = { __multipart: true, fields: { ...payload, idempotency_key: pendingKey }, file: await filePayload(attachment) };
                    await window.OfflineQueue.enqueue({ path: `/api/v1/items/${encodeURIComponent(itemId)}/entry`, body: offlineBody, idempotencyKey: pendingKey });
                    window.OfflineQueue.setAccessToken(token);
                    pendingFingerprint = null;
                    pendingKey = null;
                    form.reset();
                    itemSelect.value = "";
                    updateItemSummary();
                    setMessage("Sem conexão. A entrada e a nota foram guardadas neste dispositivo e serão sincronizadas quando a conexão voltar.");
                    return;
                } catch (queueError) {
                    setMessage(queueError.message || "Não foi possível guardar a entrada offline.", true);
                    return;
                }
            }
            setMessage(`${error.message || "Não foi possível registrar a entrada."} Reenvie sem alterar os dados para reutilizar a mesma chave.`, true);
        } finally {
            setBusy(false);
        }
    });

    logoutButton.addEventListener("click", async () => {
        if (busy || !token) return;
        setBusy(true);
        try {
            await api("/api/v1/auth/logout", { method: "POST" });
        } catch { /* Limpa a sessão local mesmo se o servidor estiver indisponível. */ }
        clearSession();
        setMessage("Sessão encerrada.");
        setBusy(false);
    });
})();
