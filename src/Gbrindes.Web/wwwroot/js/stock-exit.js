(() => {
    const page = document.getElementById("stock-exit-page");
    if (!page) return;

    const loginForm = document.getElementById("stock-exit-login-form");
    const emailInput = document.getElementById("stock-exit-email");
    const passwordInput = document.getElementById("stock-exit-password");
    const loginButton = document.getElementById("stock-exit-login");
    const sessionPanel = document.getElementById("stock-exit-session");
    const userLabel = document.getElementById("stock-exit-user-label");
    const logoutButton = document.getElementById("stock-exit-logout");
    const formPanel = document.getElementById("stock-exit-form-panel");
    const itemSearchForm = document.getElementById("stock-exit-item-search");
    const itemQuery = document.getElementById("stock-exit-item-query");
    const searchButton = document.getElementById("stock-exit-search-button");
    const form = document.getElementById("stock-exit-form");
    const fields = document.getElementById("stock-exit-fields");
    const itemSelect = document.getElementById("stock-exit-item");
    const industrySelect = document.getElementById("stock-exit-industry");
    const departmentSelect = document.getElementById("stock-exit-department");
    const industryField = document.getElementById("stock-exit-industry-field");
    const departmentField = document.getElementById("stock-exit-department-field");
    const submitButton = document.getElementById("stock-exit-submit");
    const message = document.getElementById("stock-exit-message");
    const resultPanel = document.getElementById("stock-exit-result");
    const numberFormat = new Intl.NumberFormat("pt-BR");
    let token = null;
    let busy = false;
    let pendingFingerprint = null;
    let pendingKey = null;
    let canViewLookups = false;
    let itemsById = new Map();

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    async function filePayload(file) {
        const bytes = new Uint8Array(await file.arrayBuffer());
        let binary = "";
        for (let offset = 0; offset < bytes.length; offset += 0x8000)
            binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
        return { field: "attachment", name: file.name, type: file.type || "application/octet-stream", dataBase64: btoa(binary) };
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        logoutButton.disabled = value;
        searchButton.disabled = value;
        fields.disabled = value;
    }

    async function responseData(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const fieldMessages = data.errors && Object.values(data.errors).flat().join(" ");
            const error = new Error(fieldMessages || data.message || data.detail || data.title || data.error?.message || `Falha na requisição (${response.status}).`);
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
        for (const row of rows) select.add(new Option(label(row), String(row.id)));
        if (rows.some(row => String(row.id) === current)) select.value = current;
    }

    async function fetchIndustries() {
        const first = await api("/api/v1/industries?page=1&per_page=100");
        const rows = Array.isArray(first.items) ? [...first.items] : [];
        const pageSize = Math.max(1, Number(first.per_page) || 100);
        const totalPages = Math.ceil((Number(first.total) || rows.length) / pageSize);
        for (let pageNumber = 2; pageNumber <= totalPages; pageNumber++) {
            const next = await api(`/api/v1/industries?page=${pageNumber}&per_page=${pageSize}`);
            rows.push(...(Array.isArray(next.items) ? next.items : []));
        }
        return rows;
    }

    async function loadItems(query = "") {
        const suffix = query.trim() ? `?q=${encodeURIComponent(query.trim())}` : "";
        const response = await api(`/api/v1/items/options${suffix}`);
        const rows = Array.isArray(response.data) ? response.data : [];
        itemsById = new Map(rows.map(row => [String(row.id), row]));
        setOptions(itemSelect, rows, rows.length ? "Selecione um brinde" : "Nenhum brinde encontrado",
            row => `${row.code} — ${row.name} · disponível total ${numberFormat.format(row.available || 0)}`);
        if (!itemsById.has(itemSelect.value)) itemSelect.value = "";
    }

    function updateItemDescription() {
        const summary = document.getElementById("stock-exit-item-summary");
        const item = itemsById.get(itemSelect.value);
        summary.textContent = item
            ? `${item.code} — ${item.name}. Disponível no saldo geral para reserva: ${numberFormat.format(item.available || 0)}.`
            : "O saldo disponível é agregado; a API valida a reserva antes de criar a ordem.";
    }

    async function loadOptions() {
        const tasks = [loadItems()];
        if (canViewLookups) {
            industryField.classList.remove("d-none");
            departmentField.classList.remove("d-none");
            tasks.push((async () => {
                const industries = await fetchIndustries();
                setOptions(industrySelect, industries, "Não informado", row => row.name);
            })());
            tasks.push((async () => {
                const departments = await api("/api/v1/departments?all=1");
                setOptions(departmentSelect, Array.isArray(departments) ? departments : [], "Não informado", row => row.name);
            })());
        }
        const results = await Promise.allSettled(tasks);
        const failed = results.find(result => result.status === "rejected");
        if (failed) setMessage(`Brindes carregados quando disponíveis; uma lista opcional falhou: ${failed.reason?.message || "falha de rede"}.`, true);
        formPanel.classList.remove("d-none");
        updateItemDescription();
        if (!failed) setMessage("Cadastros carregados. O saldo será reservado pela API; a baixa exige confirmação CD.");
    }

    function newIdempotencyKey() {
        return globalThis.crypto?.randomUUID
            ? globalThis.crypto.randomUUID()
            : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
    }

    async function hashFile(file) {
        if (!globalThis.crypto?.subtle)
            throw new Error("Este navegador não oferece hash seguro para repetir o envio idempotente do anexo.");
        const digest = await globalThis.crypto.subtle.digest("SHA-256", await file.arrayBuffer());
        return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("").toUpperCase();
    }

    async function downloadAttachment(path, button) {
        button.disabled = true;
        try {
            const response = await fetch(path, { cache: "no-store", headers: { Authorization: `Bearer ${token}` } });
            if (!response.ok) throw new Error(`Falha ao baixar a nota (${response.status}).`);
            const blobUrl = URL.createObjectURL(await response.blob());
            const link = document.createElement("a");
            link.href = blobUrl;
            const disposition = response.headers.get("Content-Disposition") || "";
            link.download = disposition.match(/filename\*?=(?:UTF-8''|\")?([^\";]+)/i)?.[1] || "nota-saida";
            link.click();
            setTimeout(() => URL.revokeObjectURL(blobUrl), 1000);
        } catch (error) {
            setMessage(error.message || "Não foi possível baixar a nota.", true);
        } finally {
            button.disabled = false;
        }
    }

    function showCreatedOrder(order, stock, detail) {
        resultPanel.replaceChildren();
        resultPanel.classList.remove("d-none");
        const heading = document.createElement("h2");
        heading.className = "h5";
        heading.textContent = `Saída autorizada ${order.code || ""}`;
        resultPanel.append(heading);
        const status = document.createElement("p");
        status.textContent = `Reserva criada. Disponível após a reserva: ${numberFormat.format(stock?.available ?? 0)}. O CD ainda precisa confirmar a baixa.`;
        resultPanel.append(status);
        if (detail?.qr_png && detail.qr_png.startsWith("data:image/png;base64,")) {
            const image = document.createElement("img");
            image.src = detail.qr_png;
            image.alt = `QR da saída ${order.code || "autorizada"}`;
            image.width = 180;
            image.height = 180;
            image.className = "bg-white p-2 rounded";
            image.style.imageRendering = "pixelated";
            resultPanel.append(image);
        }
        const attachmentUrl = detail?.attachment_url || order.attachment_url;
        if (attachmentUrl) {
            const download = document.createElement("button");
            download.type = "button";
            download.className = "btn btn-sm btn-outline-secondary d-block mt-2";
            download.textContent = "Baixar NF anexada";
            download.addEventListener("click", () => downloadAttachment(attachmentUrl, download));
            resultPanel.append(download);
        }
        const link = document.createElement("a");
        link.className = "btn btn-sm btn-outline-primary d-block mt-2";
        link.href = "/stock/queue";
        link.textContent = "Abrir fila de confirmação CD";
        resultPanel.append(link);
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
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de autorizar saídas.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            const permissions = Array.isArray(data.permissions) ? data.permissions : [];
            const missing = ["stock.exit", "items.view"].filter(permission => !permissions.includes(permission));
            if (missing.length) throw new Error(`Permissões necessárias para esta tela: ${missing.join(", ")}.`);
            canViewLookups = permissions.includes("lookups.view");
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
            canViewLookups = false;
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
            await loadItems(itemQuery.value);
            updateItemDescription();
            setMessage(itemsById.size ? `${itemsById.size} brinde(s) disponíveis nesta busca.` : "Nenhum brinde encontrado.");
        } catch (error) {
            setMessage(error.message || "Não foi possível buscar os brindes.", true);
        } finally {
            setBusy(false);
        }
    });

    itemSelect.addEventListener("change", updateItemDescription);

    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token || !form.reportValidity()) return;
        const item = itemsById.get(itemSelect.value);
        if (!item) {
            setMessage("Selecione um brinde válido da busca.", true);
            return;
        }
        const body = {
            item_id: item.id,
            quantity: Number(document.getElementById("stock-exit-quantity").value),
            purpose: document.getElementById("stock-exit-purpose").value.trim(),
            recipient: document.getElementById("stock-exit-recipient").value.trim() || null,
            industry_id: canViewLookups ? (industrySelect.value || null) : null,
            department_id: canViewLookups ? (departmentSelect.value || null) : null,
            document_ref: document.getElementById("stock-exit-document").value.trim() || null,
            notes: document.getElementById("stock-exit-notes").value.trim() || null
        };
        const attachment = document.getElementById("stock-exit-attachment").files?.[0] || null;
        if (attachment && attachment.size > 10 * 1024 * 1024) {
            setMessage("A nota deve ter até 10 MB.", true);
            return;
        }
        if (!window.confirm(`Reservar ${body.quantity} unidade(s) de ${item.code} para “${body.purpose}”? O CD/Estoque precisará confirmar a baixa depois.`)) return;
        setBusy(true);
        resultPanel.replaceChildren();
        resultPanel.classList.add("d-none");
        setMessage("Criando ordem e reservando saldo…");
        try {
            const attachmentSha256 = attachment ? await hashFile(attachment) : null;
            const fingerprint = JSON.stringify({ body, attachmentSha256 });
            if (fingerprint !== pendingFingerprint || !pendingKey) {
                pendingFingerprint = fingerprint;
                pendingKey = newIdempotencyKey();
            }
            let requestBody = JSON.stringify(body);
            const headers = { "Content-Type": "application/json", "Idempotency-Key": pendingKey };
            if (attachment) {
                const multipart = new FormData();
                for (const [name, value] of Object.entries(body))
                    if (value !== null && value !== undefined) multipart.append(name, String(value));
                multipart.append("attachment", attachment, attachment.name);
                requestBody = multipart;
                delete headers["Content-Type"];
            }
            const response = await api("/api/v1/stock/exits", {
                method: "POST",
                headers,
                body: requestBody
            });
            const order = response.data?.order || {};
            const stock = response.data?.stock || {};
            pendingFingerprint = null;
            pendingKey = null;
            let detail = null;
            if (order.id) {
                try {
                    detail = (await api(`/api/v1/stock/exit-orders/${encodeURIComponent(order.id)}`)).data;
                } catch {
                    detail = null;
                }
            }
            showCreatedOrder(order, stock, detail);
            setMessage("Ordem autorizada e saldo reservado. A baixa só ocorre após confirmação CD.");
            form.reset();
            itemQuery.value = "";
            itemSelect.value = "";
            updateItemDescription();
        } catch (error) {
            if ((!error.status || error.status === 0) && attachment && window.OfflineQueue?.canQueue("POST", "/api/v1/stock/exits", {
                __multipart: true,
                fields: body,
                file: { field: "attachment", name: attachment.name, type: attachment.type || "application/octet-stream", dataBase64: "pending" }
            }, pendingKey)) {
                try {
                    const offlineBody = { __multipart: true, fields: body, file: await filePayload(attachment) };
                    await window.OfflineQueue.enqueue({ path: "/api/v1/stock/exits", body: offlineBody, idempotencyKey: pendingKey });
                    window.OfflineQueue.setAccessToken(token);
                    pendingFingerprint = null;
                    pendingKey = null;
                    form.reset();
                    setMessage("Sem conexão. A saída e o anexo foram guardados neste dispositivo e serão sincronizados quando a conexão voltar.");
                    return;
                } catch (queueError) {
                    setMessage(queueError.message || "Não foi possível guardar a saída offline.", true);
                    return;
                }
            }
            setMessage(error.message || "Não foi possível criar a ordem. Uma tentativa com os mesmos dados reutilizará a chave idempotente.", true);
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
            canViewLookups = false;
            itemsById.clear();
            form.reset();
            itemQuery.value = "";
            formPanel.classList.add("d-none");
            resultPanel.replaceChildren();
            resultPanel.classList.add("d-none");
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });
})();
