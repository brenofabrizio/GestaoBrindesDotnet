(() => {
    const page = document.getElementById("event-detail-page");
    if (!page) return;
    const eventId = page.dataset.eventId;
    const loginForm = document.getElementById("event-detail-login-form");
    const email = document.getElementById("event-detail-email");
    const password = document.getElementById("event-detail-password");
    const session = document.getElementById("event-detail-session");
    const userLabel = document.getElementById("event-detail-user-label");
    const message = document.getElementById("event-detail-message");
    const content = document.getElementById("event-detail-content");
    const managementActions = document.getElementById("event-detail-management-actions");
    const managementCapability = document.getElementById("event-detail-management-capability");
    const openEventButton = document.getElementById("event-detail-open");
    const closeEventButton = document.getElementById("event-detail-close");
    const returnForm = document.getElementById("event-detail-return-form");
    const returnSubmit = document.getElementById("event-detail-return-submit");
    const withdrawalForm = document.getElementById("event-detail-withdrawal-form");
    const withdrawalControls = document.getElementById("event-detail-withdrawal-controls");
    const withdrawalIndustry = document.getElementById("event-detail-withdrawal-industry");
    const withdrawalLines = document.getElementById("event-detail-withdrawal-lines");
    const withdrawalSubmit = document.getElementById("event-detail-withdrawal-submit");
    const allocationEditor = document.getElementById("event-detail-allocation-editor");
    const allocationForm = document.getElementById("event-detail-allocation-form");
    const allocationMessage = document.getElementById("event-detail-allocation-message");
    const allocationControls = document.getElementById("event-detail-allocation-controls");
    const allocationIndustrySearch = document.getElementById("event-detail-allocation-industry-search");
    const allocationIndustry = document.getElementById("event-detail-allocation-industry");
    const allocationItemSearch = document.getElementById("event-detail-allocation-item-search");
    const allocationItem = document.getElementById("event-detail-allocation-item");
    const allocationQuantity = document.getElementById("event-detail-allocation-quantity");
    const allocationAdd = document.getElementById("event-detail-allocation-add");
    const allocationRows = document.getElementById("event-detail-allocation-rows");
    const allocationSubmit = document.getElementById("event-detail-allocation-submit");
    const withdrawalSignature = document.getElementById("event-detail-withdrawal-signature");
    const withdrawalSignatureContext = withdrawalSignature.getContext("2d");
    const withdrawalSignatureClear = document.getElementById("event-detail-withdrawal-signature-clear");
    const loginButton = document.getElementById("event-detail-login");
    const logoutButton = document.getElementById("event-detail-logout");
    let token = null;
    let permissions = [];
    let isAdmin = false;
    let busy = false;
    let pendingReturnFingerprint = null;
    let pendingReturnKey = null;
    let pendingWithdrawalFingerprint = null;
    let pendingWithdrawalKey = null;
    let currentAllocations = [];
    let currentEventStatus = "";
    let hasLoadedEvent = false;
    let hasWithdrawalSignature = false;
    let withdrawalPointerActive = false;
    let allocationDraft = [];
    let allocationIndustries = [];
    let allocationItems = [];
    let allocationOptionsReady = false;
    let allocationIndustrySearchTimer = null;
    let allocationItemSearchTimer = null;
    let allocationIndustrySearchVersion = 0;
    let allocationItemSearchVersion = 0;

    function say(text, error = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", error);
        message.classList.toggle("text-secondary", !error);
    }
    async function readResponse(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const fieldErrors = data.errors && Object.values(data.errors).flat().join(" ");
            throw new Error(fieldErrors || data.message || data.title || data.error?.message || data.error || `Falha na requisição (${response.status}).`);
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
    function createIdempotencyKey() {
        return crypto.randomUUID().replaceAll("-", "");
    }
    function hasReturnQuantity() {
        return [...document.querySelectorAll("#event-detail-returns input[type=number]")]
            .some(input => Number.isInteger(Number(input.value)) && Number(input.value) > 0 && Number(input.value) <= Number(input.max));
    }
    function hasWithdrawalQuantity() {
        return [...withdrawalLines.querySelectorAll("input[type=number]")]
            .some(input => Number.isInteger(Number(input.value)) && Number(input.value) > 0 && Number(input.value) <= Number(input.max));
    }
    function updateWithdrawalSubmit() {
        withdrawalSubmit.disabled = busy || !withdrawalIndustry.value || !hasWithdrawalQuantity() || !hasWithdrawalSignature;
    }
    function updateManagementActions() {
        const canManage = canManageEvents();
        const statusAllowsManagement = currentEventStatus === "planejado" || currentEventStatus === "aberto";
        managementActions.classList.toggle("d-none", !canManage || !statusAllowsManagement);
        managementCapability.classList.toggle("d-none", canManage || !statusAllowsManagement);
        openEventButton.classList.toggle("d-none", !canManage || currentEventStatus !== "planejado");
        closeEventButton.classList.toggle("d-none", !canManage || currentEventStatus !== "aberto");
        openEventButton.disabled = busy || !canManage || currentEventStatus !== "planejado";
        closeEventButton.disabled = busy || !canManage || currentEventStatus !== "aberto";
    }
    function canManageEvents() {
        return permissions.includes("events.manage") || isAdmin;
    }
    function canEditEventAllocations() {
        return Boolean(token) && canManageEvents() && currentEventStatus === "planejado";
    }
    function updateAllocationControls() {
        const canEdit = canEditEventAllocations();
        allocationEditor.classList.toggle("d-none", !canEdit);
        allocationControls.disabled = busy || !canEdit || !allocationOptionsReady;
        allocationSubmit.disabled = busy || !canEdit || !allocationOptionsReady;
    }
    function setAllocationMessage(text, error = false) {
        allocationMessage.textContent = text;
        allocationMessage.classList.toggle("text-danger", error);
        allocationMessage.classList.toggle("text-secondary", !error);
    }
    function setAllocationSelect(select, values, placeholder, labelFor) {
        const previous = select.value;
        const empty = document.createElement("option");
        empty.value = "";
        empty.textContent = placeholder;
        select.replaceChildren(empty);
        for (const value of values) {
            const option = document.createElement("option");
            option.value = String(value.id);
            option.textContent = labelFor(value);
            select.append(option);
        }
        if (values.some(value => String(value.id) === previous)) select.value = previous;
    }
    function renderAllocationDraft() {
        allocationRows.replaceChildren();
        if (!allocationDraft.length) {
            const empty = document.createElement("p");
            empty.className = "small text-secondary mb-0";
            empty.textContent = "Nenhuma cota configurada. Inclua uma linha antes de abrir o evento.";
            allocationRows.append(empty);
            return;
        }
        for (const [index, row] of allocationDraft.entries()) {
            const line = document.createElement("div");
            line.className = "row g-2 align-items-center border-bottom py-2";
            const label = document.createElement("label");
            label.className = "col-md-7 mb-0";
            label.htmlFor = `event-detail-allocation-row-${index}`;
            label.textContent = `${row.industry?.name || "Indústria"} · ${row.item?.code || ""} ${row.item?.name || "Brinde"}`.trim();
            const quantity = document.createElement("input");
            quantity.className = "form-control col-md-3";
            quantity.id = label.htmlFor;
            quantity.type = "number";
            quantity.min = "1";
            quantity.max = "1000000";
            quantity.step = "1";
            quantity.value = String(row.qty_allocated ?? 1);
            quantity.setAttribute("aria-label", `Quantidade da cota: ${label.textContent}`);
            quantity.addEventListener("input", () => {
                row.qty_allocated = quantity.value;
                updateAllocationControls();
            });
            const remove = document.createElement("button");
            remove.className = "btn btn-sm btn-outline-danger col-md-2";
            remove.type = "button";
            remove.textContent = "Remover";
            remove.setAttribute("aria-label", `Remover cota: ${label.textContent}`);
            remove.addEventListener("click", () => {
                allocationDraft = allocationDraft.filter(value => value !== row);
                renderAllocationDraft();
            });
            line.append(label, quantity, remove);
            allocationRows.append(line);
        }
    }
    async function loadAllocationLookupOptions() {
        if (!canEditEventAllocations() || allocationOptionsReady) return;
        allocationControls.disabled = true;
        setAllocationMessage("Carregando indústrias e brindes ativos…");
        try {
            const [industryResponse, itemResponse] = await Promise.all([
                api("/api/v1/industries?active=1&per_page=100&page=1"),
                api("/api/v1/items/options")
            ]);
            allocationIndustries = Array.isArray(industryResponse.items) ? industryResponse.items : [];
            allocationItems = Array.isArray(itemResponse.data) ? itemResponse.data : [];
            setAllocationSelect(allocationIndustry, allocationIndustries, "Selecione uma indústria", value => value.name || "Indústria");
            setAllocationSelect(allocationItem, allocationItems, "Selecione um brinde", value => `${value.code || ""} — ${value.name || "Brinde"}`.trim());
            allocationOptionsReady = true;
            setAllocationMessage("Listas mostram até 100 indústrias e 50 brindes; use os campos de busca para filtrar.");
        } catch (error) {
            allocationOptionsReady = false;
            setAllocationMessage(`A edição exige lookups.view e items.view para consultar as opções. ${error.message || "Não foi possível carregar as opções."}`, true);
        } finally {
            updateAllocationControls();
        }
    }
    function scheduleAllocationIndustrySearch() {
        window.clearTimeout(allocationIndustrySearchTimer);
        const version = ++allocationIndustrySearchVersion;
        allocationIndustrySearchTimer = window.setTimeout(async () => {
            try {
                const params = new URLSearchParams({ active: "1", per_page: "100", page: "1" });
                if (allocationIndustrySearch.value.trim()) params.set("q", allocationIndustrySearch.value.trim());
                const response = await api(`/api/v1/industries?${params}`);
                if (version !== allocationIndustrySearchVersion) return;
                allocationIndustries = Array.isArray(response.items) ? response.items : [];
                setAllocationSelect(allocationIndustry, allocationIndustries, "Selecione uma indústria", value => value.name || "Indústria");
                setAllocationMessage(`${allocationIndustries.length} indústria(s) encontrada(s); máximo de 100 por busca.`);
            } catch (error) {
                if (version === allocationIndustrySearchVersion)
                    setAllocationMessage(error.message || "Não foi possível buscar as indústrias.", true);
            }
        }, 250);
    }
    function scheduleAllocationItemSearch() {
        window.clearTimeout(allocationItemSearchTimer);
        const version = ++allocationItemSearchVersion;
        allocationItemSearchTimer = window.setTimeout(async () => {
            try {
                const params = new URLSearchParams();
                if (allocationItemSearch.value.trim()) params.set("q", allocationItemSearch.value.trim());
                const response = await api(`/api/v1/items/options?${params}`);
                if (version !== allocationItemSearchVersion) return;
                allocationItems = Array.isArray(response.data) ? response.data : [];
                setAllocationSelect(allocationItem, allocationItems, "Selecione um brinde", value => `${value.code || ""} — ${value.name || "Brinde"}`.trim());
                setAllocationMessage(`${allocationItems.length} brinde(s) encontrado(s); máximo de 50 por busca.`);
            } catch (error) {
                if (version === allocationItemSearchVersion)
                    setAllocationMessage(error.message || "Não foi possível buscar os brindes.", true);
            }
        }, 250);
    }
    function addAllocationRow() {
        if (busy || !canEditEventAllocations() || !allocationOptionsReady) return;
        const industry = allocationIndustries.find(value => String(value.id) === allocationIndustry.value);
        const item = allocationItems.find(value => String(value.id) === allocationItem.value);
        const quantity = Number(allocationQuantity.value);
        if (!industry || !item) {
            setAllocationMessage("Selecione uma indústria e um brinde ativos.", true);
            return;
        }
        if (!Number.isInteger(quantity) || quantity < 1 || quantity > 1_000_000) {
            setAllocationMessage("Informe uma quantidade inteira entre 1 e 1000000.", true);
            allocationQuantity.focus();
            return;
        }
        if (allocationDraft.some(row => String(row.industry?.id) === String(industry.id) && String(row.item?.id) === String(item.id))) {
            setAllocationMessage("Esta combinação de indústria e brinde já está na lista. Altere a quantidade na própria linha.", true);
            return;
        }
        allocationDraft.push({
            industry: { id: industry.id, name: industry.name },
            item: { id: item.id, code: item.code, name: item.name },
            qty_allocated: quantity,
            qty_withdrawn: 0,
            saldo: quantity
        });
        allocationIndustry.value = "";
        allocationItem.value = "";
        allocationQuantity.value = "1";
        renderAllocationDraft();
        setAllocationMessage("Cota incluída no rascunho. Salve para substituir as cotas do evento.");
    }
    function createIdempotencyKey() {
        if (window.crypto?.randomUUID) return window.crypto.randomUUID().replaceAll("-", "");
        const bytes = new Uint8Array(24);
        window.crypto.getRandomValues(bytes);
        return [...bytes].map(value => value.toString(16).padStart(2, "0")).join("");
    }
    function clearWithdrawalSignature() {
        withdrawalSignatureContext.clearRect(0, 0, withdrawalSignature.width, withdrawalSignature.height);
        hasWithdrawalSignature = false;
        updateWithdrawalSubmit();
    }
    function withdrawalPointerPoint(event) {
        const rect = withdrawalSignature.getBoundingClientRect();
        return {
            x: (event.clientX - rect.left) * withdrawalSignature.width / rect.width,
            y: (event.clientY - rect.top) * withdrawalSignature.height / rect.height
        };
    }
    function renderWithdrawalLines(allocations) {
        withdrawalLines.replaceChildren();
        const industryId = withdrawalIndustry.value;
        const rows = allocations.filter(allocation => allocation.industry?.id === industryId && Number(allocation.saldo) > 0);
        if (!rows.length) {
            const empty = document.createElement("p");
            empty.className = "small text-secondary mb-0";
            empty.textContent = industryId ? "Não há saldo de cota para retirar nesta indústria." : "Selecione uma indústria com saldo de cota.";
            withdrawalLines.append(empty);
            updateWithdrawalSubmit();
            return;
        }
        for (const allocation of rows) {
            const line = document.createElement("div");
            line.className = "d-flex flex-wrap align-items-center justify-content-between gap-2 border-bottom py-2";
            const label = document.createElement("label");
            label.className = "form-label mb-0";
            const text = `${allocation.item?.code || ""} ${allocation.item?.name || "Brinde"} (disponível na cota: ${allocation.saldo})`.trim();
            label.textContent = text;
            const quantity = document.createElement("input");
            quantity.className = "form-control";
            quantity.type = "number";
            quantity.min = "0";
            quantity.max = String(allocation.saldo);
            quantity.step = "1";
            quantity.value = "0";
            quantity.dataset.itemId = allocation.item?.id || "";
            quantity.setAttribute("aria-label", `Quantidade retirada: ${text}`);
            quantity.addEventListener("input", updateWithdrawalSubmit);
            label.htmlFor = `event-detail-withdrawal-qty-${allocation.item?.id || "item"}`;
            quantity.id = label.htmlFor;
            line.append(label, quantity);
            withdrawalLines.append(line);
        }
        updateWithdrawalSubmit();
    }
    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        logoutButton.disabled = value;
        withdrawalControls.disabled = value;
        updateManagementActions();
        updateAllocationControls();
        updateWithdrawalSubmit();
    }
    function row(label, value) {
        const line = document.createElement("div");
        line.className = "d-flex flex-wrap justify-content-between gap-2 border-bottom py-2";
        const left = document.createElement("span");
        left.textContent = label;
        const right = document.createElement("span");
        right.className = "fw-semibold";
        right.textContent = value;
        line.append(left, right);
        return line;
    }
    function render(event) {
        document.getElementById("event-detail-name").textContent = event.name || "Evento";
        document.getElementById("event-detail-status").textContent = event.status_label || event.status || "—";
        document.getElementById("event-detail-summary").textContent = [event.venue, event.starts_on, event.ends_on].filter(Boolean).join(" · ") || "Sem local ou datas informados.";
        document.getElementById("event-detail-description").textContent = event.description || "Sem descrição.";
        const allocations = document.getElementById("event-detail-allocations");
        allocations.replaceChildren();
        const rows = Array.isArray(event.allocations) ? event.allocations : [];
        currentAllocations = rows;
        currentEventStatus = event.status || "";
        hasLoadedEvent = true;
        updateManagementActions();
        allocationDraft = canEditEventAllocations() ? rows.map(allocation => ({ ...allocation })) : [];
        renderAllocationDraft();
        updateAllocationControls();
        if (!rows.length) {
            const empty = document.createElement("p");
            empty.className = "text-secondary mb-0";
            empty.textContent = "Ainda não há alocações.";
            allocations.append(empty);
        } else {
            for (const allocation of rows) {
                allocations.append(row(`${allocation.industry?.name || "Indústria"} · ${allocation.item?.code || ""} ${allocation.item?.name || "Brinde"}`.trim(), `${allocation.qty_withdrawn || 0}/${allocation.qty_allocated || 0} retirados · saldo ${allocation.saldo ?? 0}`));
            }
        }
        const returns = document.getElementById("event-detail-returns");
        returns.replaceChildren();
        const pending = Array.isArray(event.returns) ? event.returns : [];
        returnForm.classList.toggle("d-none", !["aberto", "encerrado"].includes(event.status));
        returnSubmit.disabled = true;
        if (!pending.length) {
            const empty = document.createElement("p");
            empty.className = "text-secondary mb-0";
            empty.textContent = "Nada pendente de devolução neste evento.";
            returns.append(empty);
        } else {
            for (const allocation of pending) {
                const line = document.createElement("div");
                line.className = "d-flex flex-wrap align-items-center justify-content-between gap-2 border-bottom py-2";
                const label = document.createElement("span");
                label.textContent = `${allocation.industry?.name || "Indústria"} · ${allocation.item?.code || ""} ${allocation.item?.name || "Brinde"} (saldo ${allocation.saldo ?? 0})`.trim();
                const quantity = document.createElement("input");
                quantity.className = "form-control";
                quantity.type = "number";
                quantity.min = "0";
                quantity.max = String(allocation.saldo ?? 0);
                quantity.step = "1";
                quantity.value = String(allocation.saldo ?? 0);
                quantity.dataset.industryId = allocation.industry?.id || "";
                quantity.dataset.itemId = allocation.item?.id || "";
                quantity.setAttribute("aria-label", `Quantidade devolvida: ${label.textContent}`);
                quantity.addEventListener("input", () => {
                    returnSubmit.disabled = !hasReturnQuantity();
                });
                line.append(label, quantity);
                returns.append(line);
            }
            returnSubmit.disabled = !hasReturnQuantity();
        }
        const canWithdraw = event.status === "aberto" && permissions.includes("events.withdraw");
        withdrawalForm.classList.toggle("d-none", !canWithdraw);
        withdrawalControls.disabled = busy || !canWithdraw;
        const withdrawalCapability = document.getElementById("event-detail-withdrawal-capability");
        const showWithdrawalCapability = event.status === "aberto" && !permissions.includes("events.withdraw");
        withdrawalCapability.classList.toggle("d-none", !showWithdrawalCapability);
        if (showWithdrawalCapability)
            withdrawalCapability.textContent = "Retirada indisponível: exige a permissão explícita events.withdraw. A API continua sendo a autoridade.";
        const industries = new Map();
        for (const allocation of rows) {
            if (allocation.industry?.id) industries.set(allocation.industry.id, allocation.industry.name || "Indústria");
        }
        const previousIndustry = withdrawalIndustry.value;
        withdrawalIndustry.replaceChildren();
        const placeholder = document.createElement("option");
        placeholder.value = "";
        placeholder.textContent = "Selecione uma indústria";
        withdrawalIndustry.append(placeholder);
        for (const [id, name] of industries) {
            const option = document.createElement("option");
            option.value = id;
            option.textContent = name;
            withdrawalIndustry.append(option);
        }
        if (industries.has(previousIndustry)) withdrawalIndustry.value = previousIndustry;
        renderWithdrawalLines(rows);
        content.classList.remove("d-none");
    }
    async function load() {
        setBusy(true);
        say("Carregando evento…");
        try {
            const response = await api(`/api/events/${encodeURIComponent(eventId)}`);
            const event = response.data;
            render(event);
            await loadAllocationLookupOptions();
            say("Detalhes carregados pela API.");
            return true;
        } catch (error) {
            if (!hasLoadedEvent) content.classList.add("d-none");
            say(error.message || "Não foi possível carregar o evento.", true);
            return false;
        } finally { setBusy(false); }
    }
    async function transitionEvent(action) {
        const canManage = permissions.includes("events.manage") || isAdmin;
        const statusAllowsAction = action === "open"
            ? currentEventStatus === "planejado"
            : action === "close" && currentEventStatus === "aberto";
        if (busy || !token || !canManage || !statusAllowsAction) return;
        const opening = action === "open";
        const confirmation = opening
            ? "Abrir este evento? A API reservará no estoque as quantidades alocadas."
            : "Encerrar este evento? A API liberará somente as quantidades não retiradas; isso não registra devolução física.";
        if (!window.confirm(confirmation)) return;

        setBusy(true);
        say(opening ? "Abrindo evento e reservando as cotas…" : "Encerrando evento e liberando o saldo não retirado…");
        try {
            await api(`/api/v1/events/${encodeURIComponent(eventId)}/${action}`, { method: "POST" });
            if (!await load())
                throw new Error("A ação foi processada, mas o estado não pôde ser atualizado. Atualize a página antes de repetir.");
            say(opening
                ? "Evento aberto; as cotas foram reservadas pela API."
                : "Evento encerrado; o saldo não retirado foi liberado. A devolução física deve ser registrada separadamente.");
        } catch (error) {
            say(`${error.message || "Não foi possível atualizar o evento."} Atualize a página antes de repetir a ação.`, true);
        } finally {
            setBusy(false);
        }
    }
    allocationIndustrySearch.addEventListener("input", scheduleAllocationIndustrySearch);
    allocationItemSearch.addEventListener("input", scheduleAllocationItemSearch);
    allocationAdd.addEventListener("click", addAllocationRow);
    allocationForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token || !canEditEventAllocations() || !allocationOptionsReady) return;
        const seen = new Set();
        const lines = [];
        for (const row of allocationDraft) {
            const industryId = row.industry?.id;
            const itemId = row.item?.id;
            const quantity = Number(row.qty_allocated);
            const key = `${industryId}:${itemId}`;
            if (!industryId || !itemId || !Number.isInteger(quantity) || quantity < 1 || quantity > 1_000_000) {
                setAllocationMessage("Cada cota precisa de indústria, brinde e quantidade inteira entre 1 e 1000000.", true);
                return;
            }
            if (seen.has(key)) {
                setAllocationMessage("A lista contém uma combinação repetida de indústria e brinde.", true);
                return;
            }
            seen.add(key);
            lines.push({ industry_id: industryId, item_id: itemId, qty_allocated: quantity });
        }
        const confirmation = lines.length
            ? "Salvar cotas substituirá a lista completa do evento. Confirmar?"
            : "A lista está vazia. Confirmar a remoção de todas as cotas do evento?";
        if (!window.confirm(confirmation)) return;
        setBusy(true);
        setAllocationMessage("Salvando cotas do evento…");
        say("Salvando cotas do evento…");
        try {
            await api(`/api/events/${encodeURIComponent(eventId)}/allocations`, {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ allocations: lines })
            });
            if (!await load())
                throw new Error("A API recebeu a alteração, mas a tela não conseguiu atualizar. Atualize antes de repetir o salvamento.");
            setAllocationMessage("Cotas salvas; o evento foi atualizado pela API.");
            say("Cotas do evento salvas.");
        } catch (error) {
            setAllocationMessage(`${error.message || "Não foi possível salvar as cotas."} Atualize a página antes de repetir.`, true);
            say("Não foi possível confirmar o salvamento das cotas.", true);
        } finally {
            setBusy(false);
        }
    });
    openEventButton.addEventListener("click", () => transitionEvent("open"));
    closeEventButton.addEventListener("click", () => transitionEvent("close"));
    returnForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token) return;
        const quantities = [...document.querySelectorAll("#event-detail-returns input[type=number]")];
        const lines = [];
        for (const input of quantities) {
            if (!input.value || Number(input.value) === 0) continue;
            const quantity = Number(input.value);
            if (!Number.isInteger(quantity) || quantity < 1 || quantity > Number(input.max)
                || !input.dataset.industryId || !input.dataset.itemId) {
                say("Informe quantidades inteiras entre zero e o saldo disponível.", true);
                input.focus();
                return;
            }
            lines.push({ industry_id: input.dataset.industryId, item_id: input.dataset.itemId, qty: quantity });
        }
        if (!lines.length) {
            say("Informe ao menos uma quantidade maior que zero para devolver.", true);
            return;
        }
        const body = { items: lines };
        const fingerprint = JSON.stringify(body);
        if (!window.confirm("Confirmar a devolução física destas quantidades ao CD? A API validará o saldo e atualizará a reserva e as posições.")) return;
        if (fingerprint !== pendingReturnFingerprint || !pendingReturnKey) {
            pendingReturnFingerprint = fingerprint;
            pendingReturnKey = createIdempotencyKey();
        }
        setBusy(true);
        returnSubmit.disabled = true;
        say("Registrando devolução ao CD…");
        try {
            await readResponse(await fetch(`/api/v1/events/${encodeURIComponent(eventId)}/returns`, {
                method: "POST",
                cache: "no-store",
                headers: { Authorization: `Bearer ${token}`, Accept: "application/json", "Content-Type": "application/json", "Idempotency-Key": pendingReturnKey },
                body: JSON.stringify(body)
            }));
            if (!await load()) throw new Error("Devolução registrada, mas não foi possível atualizar os detalhes. Tente novamente sem alterar as quantidades para confirmar o resultado.");
            pendingReturnFingerprint = null;
            pendingReturnKey = null;
            say("Devolução registrada; reserva e posições foram atualizadas pela API.");
        } catch (error) {
            const path = `/api/v1/events/${encodeURIComponent(eventId)}/returns`;
            const networkFailure = !error.status || error.status === 0;
            if (networkFailure && window.OfflineQueue?.canQueue("POST", path, body, pendingReturnKey)) {
                try {
                    await window.OfflineQueue.enqueue({ path, body, idempotencyKey: pendingReturnKey });
                    window.OfflineQueue.setAccessToken(token);
                    pendingReturnFingerprint = null;
                    pendingReturnKey = null;
                    say("Sem conexão. A devolução foi guardada neste dispositivo e será sincronizada quando a conexão voltar.");
                    return;
                } catch (queueError) {
                    say(queueError.message || "Não foi possível guardar a devolução offline.", true);
                    return;
                }
            }
            say(`${error.message || "Não foi possível registrar a devolução."} Se tentar novamente sem alterar as quantidades, a mesma chave idempotente será reutilizada.`, true);
        } finally {
            setBusy(false);
            returnSubmit.disabled = !hasReturnQuantity();
        }
    });
    withdrawalIndustry.addEventListener("change", () => renderWithdrawalLines(currentAllocations));
    withdrawalForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token || !withdrawalForm.reportValidity()) return;
        if (!hasWithdrawalSignature) {
            say("A assinatura de quem recebeu é obrigatória.", true);
            return;
        }
        const industryId = withdrawalIndustry.value;
        const lines = [];
        for (const input of withdrawalLines.querySelectorAll("input[type=number]")) {
            if (!input.value || Number(input.value) === 0) continue;
            const quantity = Number(input.value);
            if (!Number.isInteger(quantity) || quantity < 1 || quantity > Number(input.max) || !input.dataset.itemId) {
                say("Informe quantidades inteiras entre zero e o saldo disponível na cota.", true);
                input.focus();
                return;
            }
            lines.push({ item_id: input.dataset.itemId, qty: quantity });
        }
        if (!industryId || !lines.length) {
            say("Selecione uma indústria e informe ao menos uma quantidade maior que zero.", true);
            return;
        }
        const body = {
            industry_id: industryId,
            received_by_name: document.getElementById("event-detail-withdrawal-recipient").value.trim(),
            received_by_email: document.getElementById("event-detail-withdrawal-email").value.trim() || null,
            received_by_document: document.getElementById("event-detail-withdrawal-document").value.trim() || null,
            notes: document.getElementById("event-detail-withdrawal-notes").value.trim() || null,
            signature: withdrawalSignature.toDataURL("image/png"),
            items: lines
        };
        const fingerprint = JSON.stringify(body);
        if (!window.confirm("Confirmar a retirada dos itens selecionados? A API validará a permissão, a cota e o saldo reservado.")) return;
        if (fingerprint !== pendingWithdrawalFingerprint || !pendingWithdrawalKey) {
            pendingWithdrawalFingerprint = fingerprint;
            pendingWithdrawalKey = createIdempotencyKey();
        }
        setBusy(true);
        say("Registrando retirada e gerando protocolo…");
        try {
            await readResponse(await fetch(`/api/v1/events/${encodeURIComponent(eventId)}/withdrawals`, {
                method: "POST",
                cache: "no-store",
                headers: { Authorization: `Bearer ${token}`, Accept: "application/json", "Content-Type": "application/json", "Idempotency-Key": pendingWithdrawalKey },
                body: JSON.stringify(body)
            }));
            if (!await load()) throw new Error("Retirada registrada, mas não foi possível atualizar o evento. Reenvie os mesmos dados para confirmar o resultado.");
            pendingWithdrawalFingerprint = null;
            pendingWithdrawalKey = null;
            withdrawalForm.reset();
            clearWithdrawalSignature();
            say("Retirada registrada pela API.");
        } catch (error) {
            const path = `/api/v1/events/${encodeURIComponent(eventId)}/withdrawals`;
            const networkFailure = !error.status || error.status === 0;
            if (networkFailure && window.OfflineQueue?.canQueue("POST", path, body, pendingWithdrawalKey)) {
                try {
                    await window.OfflineQueue.enqueue({ path, body, idempotencyKey: pendingWithdrawalKey });
                    window.OfflineQueue.setAccessToken(token);
                    pendingWithdrawalFingerprint = null;
                    pendingWithdrawalKey = null;
                    withdrawalForm.reset();
                    clearWithdrawalSignature();
                    say("Sem conexão. A retirada foi guardada neste dispositivo e será sincronizada quando a conexão voltar.");
                    return;
                } catch (queueError) {
                    say(queueError.message || "Não foi possível guardar a retirada offline.", true);
                    return;
                }
            }
            say(`${error.message || "Não foi possível registrar a retirada."} Se tentar novamente sem alterar os dados, a mesma chave idempotente será reutilizada.`, true);
        } finally {
            setBusy(false);
            updateWithdrawalSubmit();
        }
    });
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
            permissions = Array.isArray(data.permissions) ? data.permissions : [];
            isAdmin = data.isAdmin === true;
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de continuar.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            loginForm.classList.add("d-none");
            session.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            setBusy(false);
            await load();
        } catch (error) {
            if (token) fetch("/api/v1/auth/logout", { method: "POST", cache: "no-store", headers: { Authorization: `Bearer ${token}`, Accept: "application/json" } }).catch(() => {});
            token = null;
            permissions = [];
            isAdmin = false;
            updateManagementActions();
            updateAllocationControls();
            say(error.message || "Não foi possível entrar.", true);
        } finally { setBusy(false); }
    });
    logoutButton.addEventListener("click", async () => {
        if (busy || !token) return;
        setBusy(true);
        try { await fetch("/api/v1/auth/logout", { method: "POST", cache: "no-store", headers: { Authorization: `Bearer ${token}`, Accept: "application/json" } }); }
        finally {
            token = null;
            permissions = [];
            isAdmin = false;
            currentEventStatus = "";
            allocationDraft = [];
            allocationIndustries = [];
            allocationItems = [];
            allocationOptionsReady = false;
            allocationIndustrySearchVersion++;
            allocationItemSearchVersion++;
            window.clearTimeout(allocationIndustrySearchTimer);
            window.clearTimeout(allocationItemSearchTimer);
            allocationIndustrySearch.value = "";
            allocationItemSearch.value = "";
            allocationIndustry.replaceChildren();
            allocationItem.replaceChildren();
            allocationRows.replaceChildren();
            setAllocationMessage("");
            pendingReturnFingerprint = null;
            pendingReturnKey = null;
            pendingWithdrawalFingerprint = null;
            pendingWithdrawalKey = null;
            withdrawalForm.reset();
            clearWithdrawalSignature();
            content.classList.add("d-none");
            session.classList.add("d-none");
            loginForm.classList.remove("d-none");
            updateManagementActions();
            updateAllocationControls();
            say("Sessão encerrada.");
            setBusy(false);
        }
    });
    withdrawalSignature.addEventListener("pointerdown", event => {
        if (busy || !token || !permissions.includes("events.withdraw")) return;
        event.preventDefault();
        withdrawalSignature.setPointerCapture(event.pointerId);
        const point = withdrawalPointerPoint(event);
        withdrawalSignatureContext.beginPath();
        withdrawalSignatureContext.moveTo(point.x, point.y);
        withdrawalSignatureContext.lineTo(point.x + 0.1, point.y + 0.1);
        withdrawalSignatureContext.strokeStyle = "#172b4d";
        withdrawalSignatureContext.lineWidth = 9;
        withdrawalSignatureContext.lineCap = "round";
        withdrawalSignatureContext.stroke();
        withdrawalPointerActive = true;
        hasWithdrawalSignature = true;
        updateWithdrawalSubmit();
    });
    withdrawalSignature.addEventListener("pointermove", event => {
        if (!withdrawalPointerActive || busy) return;
        event.preventDefault();
        const point = withdrawalPointerPoint(event);
        withdrawalSignatureContext.lineTo(point.x, point.y);
        withdrawalSignatureContext.stroke();
        hasWithdrawalSignature = true;
        updateWithdrawalSubmit();
    });
    const endWithdrawalSignature = event => {
        if (!withdrawalPointerActive) return;
        withdrawalPointerActive = false;
        if (withdrawalSignature.hasPointerCapture(event.pointerId)) withdrawalSignature.releasePointerCapture(event.pointerId);
    };
    withdrawalSignature.addEventListener("pointerup", endWithdrawalSignature);
    withdrawalSignature.addEventListener("pointercancel", endWithdrawalSignature);
    withdrawalSignatureClear.addEventListener("click", clearWithdrawalSignature);
})();
