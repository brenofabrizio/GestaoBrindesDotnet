(() => {
    const root = document.getElementById("request-create-page");
    if (!root) return;
    const requestId = root.dataset.requestId || "";
    const editMode = root.dataset.edit === "true" && Boolean(requestId);

    const loginForm = document.getElementById("request-create-login");
    const loginButton = document.getElementById("request-create-login-button");
    const logoutButton = document.getElementById("request-create-logout");
    const form = document.getElementById("request-create-form");
    const message = document.getElementById("request-create-message");
    const errorBox = document.getElementById("request-create-error");
    const capabilityBox = document.getElementById("request-create-capability");
    const resultBox = document.getElementById("request-create-result");
    const searchInput = document.getElementById("request-item-search");
    const optionsBox = document.getElementById("request-item-options");
    const searchMessage = document.getElementById("request-item-search-message");
    const itemsBody = document.getElementById("request-selected-items");
    const itemsEmpty = document.getElementById("request-items-empty");
    const purchaseWarning = document.getElementById("request-purchase-warning");
    const createButton = document.getElementById("request-create-submit");
    const submitDraftButton = document.getElementById("request-submit-draft");
    const departmentSelect = document.getElementById("request-department");
    const industrySelect = document.getElementById("request-industry");

    let token = null;
    let user = null;
    let permissions = [];
    let items = [];
    let busy = false;
    let searchTimer = null;
    let createdId = null;
    let createdCode = "";
    let createOutcomeUncertain = false;

    const hasPermission = permission => permissions.includes(permission);

    function setMessage(text, error = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", error);
        message.classList.toggle("text-secondary", !error);
    }

    function setError(text) {
        errorBox.textContent = text;
        errorBox.classList.toggle("d-none", !text);
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        logoutButton.disabled = value;
        for (const control of form.querySelectorAll("input, select, textarea, button")) {
            if (control !== submitDraftButton) control.disabled = value || createOutcomeUncertain || Boolean(createdId);
        }
        submitDraftButton.disabled = value || !createdId;
    }

    function describeError(data, status) {
        const fields = data?.errors && Object.values(data.errors).flat().filter(Boolean).join(" ");
        return fields || data?.message || data?.error?.message || data?.detail || data?.title || `Falha na API (${status}).`;
    }

    async function responseJson(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const error = new Error(describeError(data, response.status));
            error.status = response.status;
            throw error;
        }
        return data;
    }

    async function api(path, init = {}) {
        const headers = { Accept: "application/json", ...(init.headers || {}) };
        if (token) headers.Authorization = `Bearer ${token}`;
        if (init.body && typeof init.body !== "string") {
            headers["Content-Type"] = "application/json";
            init = { ...init, body: JSON.stringify(init.body) };
        }
        return responseJson(await fetch(path, { cache: "no-store", ...init, headers }));
    }

    function addOptions(select, rows) {
        for (const row of rows) {
            const option = document.createElement("option");
            option.value = row.id;
            option.textContent = row.name;
            select.append(option);
        }
    }

    function ensureCurrentOption(select, id, name) {
        if (!id || [...select.options].some(option => option.value === String(id))) return;
        const option = document.createElement("option");
        option.value = String(id);
        option.textContent = name || String(id);
        select.append(option);
    }

    async function loadLookups() {
        document.getElementById("request-department-field").classList.remove("d-none");
        document.getElementById("request-industry-field").classList.remove("d-none");
        departmentSelect.replaceChildren(new Option(editMode ? "Nenhum departamento" : "Usar departamento do perfil", ""));
        industrySelect.replaceChildren(new Option(editMode ? "Nenhuma (o vínculo do perfil prevalece)" : "Usar indústria do perfil", ""));
        if (!hasPermission("lookups.view")) {
            document.getElementById("request-department-field").classList.add("d-none");
            document.getElementById("request-industry-field").classList.add("d-none");
            capabilityBox.textContent = "Departamento e indústria serão definidos pelo perfil da conta, pois lookups.view não está concedida.";
            capabilityBox.classList.remove("d-none");
            return;
        }
        try {
            const [departmentsResponse, firstIndustriesResponse] = await Promise.all([
                api("/api/v1/departments?all=1"),
                api("/api/v1/industries?page=1&per_page=100")
            ]);
            const departments = Array.isArray(departmentsResponse) ? departmentsResponse : (departmentsResponse.items || departmentsResponse.data || []);
            const industries = [...(firstIndustriesResponse.items || firstIndustriesResponse.data || [])];
            const industryPageSize = Number(firstIndustriesResponse.per_page) || 100;
            const industryTotal = Number(firstIndustriesResponse.total) || industries.length;
            const industryPages = Math.ceil(industryTotal / industryPageSize);
            for (let pageNumber = 2; pageNumber <= industryPages; pageNumber += 1) {
                const response = await api(`/api/v1/industries?page=${pageNumber}&per_page=${industryPageSize}`);
                industries.push(...(response.items || response.data || []));
            }
            addOptions(departmentSelect, departments);
            const assignedIndustryId = user?.industryId || user?.industry_id;
            const visibleIndustries = assignedIndustryId
                ? industries.filter(industry => String(industry.id) === String(assignedIndustryId))
                : industries;
            addOptions(industrySelect, visibleIndustries);
        } catch (error) {
            document.getElementById("request-department-field").classList.add("d-none");
            document.getElementById("request-industry-field").classList.add("d-none");
            capabilityBox.textContent = `Não foi possível carregar os cadastros; serão usados os vínculos do perfil. ${error.message}`;
            capabilityBox.classList.remove("d-none");
        }
    }

    async function searchItems() {
        const query = searchInput.value.trim();
        optionsBox.replaceChildren();
        optionsBox.classList.add("d-none");
        searchMessage.textContent = "";
        if (!query) return;
        try {
            const response = await api(`/api/v1/items/options?q=${encodeURIComponent(query)}`);
            const rows = Array.isArray(response.data) ? response.data : [];
            if (rows.length === 0) {
                searchMessage.textContent = "Nenhum brinde ativo encontrado.";
                return;
            }
            for (const item of rows) {
                if (items.some(selected => selected.id === item.id)) continue;
                const option = document.createElement("button");
                option.type = "button";
                option.className = "list-group-item list-group-item-action";
                option.setAttribute("role", "option");
                option.textContent = `${item.code} — ${item.name} (disp. ${item.available})`;
                option.addEventListener("click", () => addItem(item));
                optionsBox.append(option);
            }
            if (optionsBox.childElementCount === 0) {
                searchMessage.textContent = "Os brindes encontrados já foram adicionados.";
                return;
            }
            optionsBox.classList.remove("d-none");
        } catch (error) {
            searchMessage.textContent = error.message || "Não foi possível buscar brindes.";
        }
    }

    function addItem(item) {
        if (items.some(selected => selected.id === item.id)) return;
        items.push({ id: item.id, code: item.code, name: item.name, available: Number(item.available) || 0, qty: 1 });
        searchInput.value = "";
        optionsBox.replaceChildren();
        optionsBox.classList.add("d-none");
        searchMessage.textContent = `${item.code} adicionado.`;
        renderItems();
        searchInput.focus();
    }

    function renderItems() {
        itemsBody.replaceChildren();
        itemsEmpty.classList.toggle("d-none", items.length > 0);
        for (const item of items) {
            const row = document.createElement("tr");
            const name = document.createElement("th");
            name.scope = "row";
            name.textContent = `${item.code} — ${item.name}`;
            const available = document.createElement("td");
            available.textContent = item.available === null ? "—" : String(item.available);
            const quantityCell = document.createElement("td");
            const quantity = document.createElement("input");
            quantity.className = "form-control";
            quantity.type = "number";
            quantity.min = "1";
            quantity.max = "1000000";
            quantity.step = "1";
            quantity.value = String(item.qty);
            quantity.required = true;
            quantity.setAttribute("aria-label", `Quantidade solicitada de ${item.name}`);
            quantity.addEventListener("input", () => {
                item.qty = Number(quantity.value);
                updatePurchaseWarning();
            });
            quantityCell.append(quantity);
            const actionCell = document.createElement("td");
            const remove = document.createElement("button");
            remove.type = "button";
            remove.className = "btn btn-sm btn-outline-danger";
            remove.textContent = "Remover";
            remove.setAttribute("aria-label", `Remover ${item.name}`);
            remove.addEventListener("click", () => {
                items = items.filter(selected => selected.id !== item.id);
                renderItems();
            });
            actionCell.append(remove);
            row.append(name, available, quantityCell, actionCell);
            itemsBody.append(row);
        }
        updatePurchaseWarning();
    }

    function updatePurchaseWarning() {
        const needsPurchase = items.some(item => !Number.isInteger(item.qty) || item.qty < 1 || item.qty > 1_000_000
            || (Number.isFinite(item.available) && item.qty > item.available));
        purchaseWarning.classList.toggle("d-none", !needsPurchase);
    }

    function buildPayload() {
        const payload = {
            purpose: document.getElementById("request-purpose").value.trim(),
            recipient: document.getElementById("request-recipient").value.trim() || null,
            needed_date: document.getElementById("request-needed-date").value || null,
            purchase_ticket_no: document.getElementById("request-purchase-ticket").value.trim() || null,
            notes: document.getElementById("request-notes").value.trim() || null,
            items: items.map(item => ({ item_id: item.id, qty_requested: Number(item.qty) }))
        };
        if (hasPermission("lookups.view")) {
            payload.department_id = departmentSelect.value || null;
            payload.industry_id = industrySelect.value || null;
        }
        return payload;
    }

    async function loadExistingDraft() {
        setBusy(true);
        setMessage("Carregando rascunho…");
        try {
            const response = await api(`/api/v1/requests/${encodeURIComponent(requestId)}`);
            const request = response.data || response;
            if (!request || request.flow !== "interna" || request.status !== "rascunho"
                || String(request.requester_id) !== String(user?.id)) {
                form.classList.add("d-none");
                capabilityBox.textContent = "Este rascunho não existe, não pertence à sua conta ou já não pode ser editado.";
                capabilityBox.classList.remove("d-none");
                return;
            }
            document.getElementById("request-purpose").value = request.purpose || "";
            document.getElementById("request-recipient").value = request.recipient || "";
            document.getElementById("request-needed-date").value = String(request.needed_date || "").slice(0, 10);
            document.getElementById("request-purchase-ticket").value = request.purchase_ticket_no || "";
            document.getElementById("request-notes").value = request.notes || "";
            if (hasPermission("lookups.view")) {
                ensureCurrentOption(departmentSelect, request.department_id, request.department_name);
                ensureCurrentOption(industrySelect, request.industry_id, request.industry_name);
                departmentSelect.value = request.department_id || "";
                industrySelect.value = request.industry_id || "";
            }
            items = (Array.isArray(request.items) ? request.items : []).map(item => ({
                id: item.item_id,
                code: item.code || "",
                name: item.name || "Brinde",
                available: null,
                qty: Number(item.qty_requested)
            }));
            renderItems();
            form.classList.remove("d-none");
            document.getElementById("request-create-back").href = `/requests/${encodeURIComponent(requestId)}`;
            resultBox.classList.add("d-none");
            submitDraftButton.classList.add("d-none");
            setMessage("Rascunho carregado. A disponibilidade será recalculada ao salvar.");
        } catch (error) {
            form.classList.add("d-none");
            setError(error.message || "Não foi possível carregar o rascunho.");
            capabilityBox.textContent = "Não foi possível confirmar o estado editável do rascunho. Nenhuma alteração foi enviada.";
            capabilityBox.classList.remove("d-none");
            setMessage("Edição não iniciada.", true);
        } finally {
            setBusy(false);
        }
    }

    function showCreatedDraft() {
        resultBox.replaceChildren();
        resultBox.classList.remove("d-none");
        const text = document.createElement("p");
        text.className = "mb-2";
        text.textContent = `Rascunho ${createdCode || createdId} foi salvo. O envio ainda não foi confirmado.`;
        const link = document.createElement("a");
        link.className = "btn btn-sm btn-outline-primary";
        link.href = `/requests/${encodeURIComponent(createdId)}`;
        link.textContent = "Abrir solicitação";
        resultBox.append(text, link);
        submitDraftButton.classList.remove("d-none");
        setBusy(false);
    }

    async function sendDraft() {
        if (!createdId || busy) return;
        setError("");
        setBusy(true);
        setMessage("Enviando rascunho…");
        try {
            await api(`/api/v1/requests/${encodeURIComponent(createdId)}/submit`, { method: "POST" });
            setMessage("Solicitação enviada para processamento.");
            window.location.assign(`/requests/${encodeURIComponent(createdId)}`);
        } catch (error) {
            setError(error.message || "Não foi possível enviar o rascunho.");
            setMessage("O rascunho foi salvo, mas o envio não foi confirmado.", true);
            showCreatedDraft();
        } finally {
            setBusy(false);
        }
    }

    loginForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !loginForm.reportValidity()) return;
        setBusy(true);
        setError("");
        setMessage("Validando acesso…");
        try {
            const response = await fetch("/api/v1/auth/login", {
                method: "POST",
                cache: "no-store",
                headers: { "Content-Type": "application/json", Accept: "application/json" },
                body: JSON.stringify({
                    email: document.getElementById("request-create-email").value,
                    password: document.getElementById("request-create-password").value
                })
            });
            const data = await responseJson(response);
            token = data.accessToken || null;
            if (data.mustChangePassword || data.must_change_password) throw new Error("Esta conta precisa trocar a senha antes de criar ou editar solicitações.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            user = data.user || null;
            permissions = Array.isArray(data.permissions) ? data.permissions : [];
            document.getElementById("request-create-password").value = "";
            loginForm.classList.add("d-none");
            document.getElementById("request-create-session").classList.remove("d-none");
            document.getElementById("request-create-user").textContent = `Conectado como ${user?.name || user?.email || "usuário"}.`;
            setBusy(false);
            if (!hasPermission("requests.create")) {
                capabilityBox.textContent = "Sua conta não possui requests.create. A API continua sendo a autoridade de acesso.";
                capabilityBox.classList.remove("d-none");
                setMessage("A criação não está liberada para este perfil.", true);
                return;
            }
            if (!hasPermission("items.view")) {
                capabilityBox.textContent = "A criação requer items.view para pesquisar brindes, conforme o fluxo das fontes. Solicite o acesso ao administrador.";
                capabilityBox.classList.remove("d-none");
                setMessage("Falta permissão para consultar brindes.", true);
                return;
            }
            form.classList.remove("d-none");
            await loadLookups();
            if (editMode) {
                document.getElementById("request-create-back").href = `/requests/${encodeURIComponent(requestId)}`;
                await loadExistingDraft();
            } else {
                setMessage("Acesso validado. Pesquise e inclua os brindes necessários.");
            }
        } catch (error) {
            if (token) await fetch("/api/v1/auth/logout", { method: "POST", cache: "no-store", headers: { Authorization: `Bearer ${token}`, Accept: "application/json" } }).catch(() => {});
            token = null;
            loginForm.classList.remove("d-none");
            document.getElementById("request-create-session").classList.add("d-none");
            setError(error.message || "Não foi possível entrar.");
            setMessage("Acesso não validado.", true);
        } finally {
            setBusy(false);
        }
    });

    logoutButton.addEventListener("click", async () => {
        if (busy || !token) return;
        setBusy(true);
        try {
            await api("/api/v1/auth/logout", { method: "POST" });
        } catch {
            // A sessão local é descartada mesmo se o servidor estiver indisponível.
        } finally {
            token = null;
            user = null;
            permissions = [];
            items = [];
            createdId = null;
            createdCode = "";
            createOutcomeUncertain = false;
            form.reset();
            form.classList.add("d-none");
            loginForm.classList.remove("d-none");
            document.getElementById("request-create-session").classList.add("d-none");
            resultBox.classList.add("d-none");
            submitDraftButton.classList.add("d-none");
            capabilityBox.classList.add("d-none");
            renderItems();
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });

    searchInput.addEventListener("input", () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(searchItems, 250);
    });
    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || createdId || createOutcomeUncertain) return;
        if (!form.reportValidity()) return;
        if (items.length === 0) {
            setError("Inclua pelo menos um brinde.");
            return;
        }
        if (items.some(item => !Number.isInteger(item.qty) || item.qty < 1 || item.qty > 1_000_000)) {
            setError("Informe quantidades inteiras entre 1 e 1.000.000.");
            return;
        }
        setError("");
        setBusy(true);
        if (editMode) {
            setMessage("Salvando alterações do rascunho…");
            try {
                await api(`/api/v1/requests/${encodeURIComponent(requestId)}`, { method: "PUT", body: buildPayload() });
                setMessage("Rascunho atualizado.");
                window.location.assign(`/requests/${encodeURIComponent(requestId)}`);
            } catch (error) {
                setError(error.message || "Não foi possível salvar o rascunho.");
                setMessage("As alterações não foram confirmadas.", true);
            } finally {
                setBusy(false);
            }
            return;
        }
        setMessage("Criando e enviando solicitação…");
        try {
            const response = await api("/api/v1/requests", { method: "POST", body: buildPayload() });
            const request = response.data || {};
            createdId = request.id;
            createdCode = request.code || "";
            if (!createdId) throw new Error("A API não retornou o identificador do rascunho; confira antes de repetir o envio.");
            createButton.classList.add("d-none");
            setBusy(false);
            await sendDraft();
        } catch (error) {
            if (!createdId && error.status === undefined) {
                createOutcomeUncertain = true;
                createButton.disabled = true;
                setError("A conexão falhou durante a criação. O resultado pode ser incerto e a API não oferece chave de idempotência para este endpoint. Não repita o cadastro até conferir com a equipe responsável.");
            } else {
                setError(error.message || "Não foi possível criar a solicitação.");
            }
            setMessage("Não foi possível confirmar o fluxo de criação/envio.", true);
        } finally {
            setBusy(false);
        }
    });
    submitDraftButton.addEventListener("click", sendDraft);

    renderItems();
})();
