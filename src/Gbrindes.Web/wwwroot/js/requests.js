(() => {
    const pageRoot = document.getElementById("requests-page");
    if (!pageRoot) return;

    const requestId = pageRoot.dataset.requestId || "";
    const isDetail = requestId.length > 0;
    const loginForm = document.getElementById("requests-login-form");
    const emailInput = document.getElementById("requests-email");
    const passwordInput = document.getElementById("requests-password");
    const loginButton = document.getElementById("requests-login");
    const sessionPanel = document.getElementById("requests-session");
    const userLabel = document.getElementById("requests-user-label");
    const logoutButton = document.getElementById("requests-logout");
    const message = document.getElementById("requests-message");
    const errorPanel = document.getElementById("requests-error");
    const errorText = document.getElementById("requests-error-text");
    const retryButton = document.getElementById("requests-retry");
    const listPanel = document.getElementById("requests-list-panel");
    const detailPanel = document.getElementById("request-detail-panel");
    const searchInput = document.getElementById("requests-search");
    const statusSelect = document.getElementById("requests-status");
    const fromInput = document.getElementById("requests-from");
    const toInput = document.getElementById("requests-to");
    const listRows = document.getElementById("requests-rows");
    const countsPanel = document.getElementById("requests-counts");
    const emptyPanel = document.getElementById("requests-empty");
    const pageLabel = document.getElementById("requests-page-label");
    const previousButton = document.getElementById("requests-prev");
    const nextButton = document.getElementById("requests-next");
    const refreshButton = document.getElementById("requests-refresh");
    const filterButton = document.getElementById("requests-filter");

    const statusLabels = {
        rascunho: "Rascunho",
        solicitada: "Solicitada",
        aguardando_aprovacao: "Aguardando aprovação",
        aprovada: "Aprovada",
        em_separacao: "Em separação",
        pronta: "Pronta para retirada",
        finalizada: "Entregue / finalizada",
        reprovada: "Reprovada",
        cancelada: "Cancelada",
        compra_realizada: "Compra realizada",
        aguardando_recebimento: "Aguardando recebimento",
        recebido_cd: "Recebido no CD",
        retirado: "Retirado",
        entregue: "Entregue"
    };

    let token = null;
    let user = null;
    let permissions = [];
    let isAdmin = false;
    let busy = false;
    let pageNumber = 1;
    let lastListUrl = "/api/v1/requests?page=1&per_page=25";
    let searchTimer = null;
    let currentRequest = null;

    if (!isDetail) {
        const initialFilters = new URLSearchParams(location.search);
        searchInput.value = initialFilters.get("q") || "";
        statusSelect.value = initialFilters.get("status") || "";
        fromInput.value = initialFilters.get("from") || "";
        toInput.value = initialFilters.get("to") || "";
        pageNumber = Math.max(1, Number.parseInt(initialFilters.get("page") || "1", 10) || 1);
    }

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    function setError(text) {
        errorText.textContent = text;
        errorPanel.classList.toggle("d-none", !text);
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        logoutButton.disabled = value;
        for (const control of pageRoot.querySelectorAll("button, input, select, textarea")) {
            if (control !== loginButton && control !== logoutButton) control.disabled = value;
        }
    }

    function errorMessage(data, status) {
        const fieldErrors = data?.errors && Object.values(data.errors).flat().filter(Boolean).join(" ");
        return fieldErrors || data?.message || data?.error?.message || data?.detail || data?.title || `Falha na requisição (${status}).`;
    }

    async function readResponse(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const error = new Error(errorMessage(data, response.status));
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
        const response = await fetch(path, { cache: "no-store", ...init, headers });
        return readResponse(response);
    }

    function clearSession() {
        token = null;
        user = null;
        permissions = [];
        isAdmin = false;
        currentRequest = null;
        sessionPanel.classList.add("d-none");
        listPanel.classList.add("d-none");
        detailPanel.classList.add("d-none");
        loginForm.classList.remove("d-none");
        setError("");
    }

    function formatDate(value) {
        if (!value) return "—";
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? String(value) : new Intl.DateTimeFormat("pt-BR", { dateStyle: "short", timeStyle: "short" }).format(date);
    }

    function formatDateOnly(value) {
        if (!value) return "—";
        const match = String(value).match(/^(\d{4})-(\d{2})-(\d{2})/);
        return match ? `${match[3]}/${match[2]}/${match[1]}` : String(value);
    }

    function statusLabel(status) {
        return statusLabels[status] || status || "—";
    }

    function appendCell(row, value, className = "") {
        const cell = document.createElement("td");
        if (className) cell.className = className;
        cell.textContent = value == null || value === "" ? "—" : String(value);
        row.append(cell);
        return cell;
    }

    function queryUrl() {
        const from = fromInput.value;
        const to = toInput.value;
        if (from && to && from > to) throw new Error("A data inicial deve ser anterior ou igual à data final.");
        const params = new URLSearchParams({ page: String(pageNumber), per_page: "25" });
        if (searchInput.value.trim()) params.set("q", searchInput.value.trim());
        if (statusSelect.value) params.set("status", statusSelect.value);
        if (from) params.set("from", from);
        if (to) params.set("to", to);
        return `/api/v1/requests?${params.toString()}`;
    }

    function configureBackLink() {
        const rawReturn = new URLSearchParams(location.search).get("return");
        const returnParams = new URLSearchParams((rawReturn || "").replace(/^\?/, ""));
        const safeParams = new URLSearchParams();
        for (const key of ["q", "status", "from", "to", "page"]) {
            if (returnParams.has(key)) safeParams.set(key, returnParams.get(key));
        }
        document.getElementById("requests-back").href = `/requests${safeParams.size ? `?${safeParams.toString()}` : ""}`;
    }

    function renderCounts(counts) {
        countsPanel.replaceChildren();
        if (!counts || typeof counts !== "object") return;
        for (const [status, count] of Object.entries(counts)) {
            if (!Number.isFinite(Number(count)) || Number(count) === 0) continue;
            const badge = document.createElement("span");
            badge.className = "badge text-bg-light border";
            badge.textContent = `${status === "all" ? "Total" : statusLabel(status)}: ${count}`;
            countsPanel.append(badge);
        }
    }

    function renderRows(rows) {
        listRows.replaceChildren();
        for (const request of rows) {
            const row = document.createElement("tr");
            appendCell(row, request.code, "text-nowrap fw-semibold");
            appendCell(row, request.purpose);
            appendCell(row, request.requester?.name || request.requester_id);
            appendCell(row, request.department?.name);
            const statusCell = appendCell(row, statusLabel(request.status));
            const badge = document.createElement("span");
            badge.className = "badge text-bg-secondary";
            badge.textContent = statusLabel(request.status);
            statusCell.replaceChildren(badge);
            appendCell(row, formatDate(request.created_at), "text-nowrap small");
            const actionCell = document.createElement("td");
            const link = document.createElement("a");
            link.className = "btn btn-sm btn-outline-primary";
            const activeQuery = new URL(lastListUrl, location.origin).search;
            link.href = `/requests/${encodeURIComponent(request.id)}?return=${encodeURIComponent(activeQuery)}`;
            link.textContent = "Detalhes";
            actionCell.append(link);
            row.append(actionCell);
            listRows.append(row);
        }
        emptyPanel.classList.toggle("d-none", rows.length > 0);
    }

    async function loadList() {
        if (!token || busy) return;
        setError("");
        let url;
        try {
            url = queryUrl();
        } catch (error) {
            setError(error.message);
            return;
        }
        lastListUrl = url;
        setBusy(true);
        setMessage("Carregando solicitações…");
        try {
            const response = await api(url);
            const rows = Array.isArray(response.data) ? response.data : [];
            const meta = response.meta || {};
            renderRows(rows);
            renderCounts(meta.counts);
            const currentPage = Number(meta.page) || pageNumber;
            const perPage = Number(meta.per_page) || 25;
            const total = Number(meta.total) || 0;
            const totalPages = Math.max(1, Math.ceil(total / perPage));
            pageNumber = currentPage;
            const activeQuery = new URL(url, location.origin).search;
            window.history.replaceState(null, "", `/requests${activeQuery}`);
            pageLabel.textContent = `Página ${currentPage} de ${totalPages} · ${total} solicitação(ões)`;
            previousButton.disabled = currentPage <= 1;
            nextButton.disabled = currentPage >= totalPages;
            setMessage(rows.length ? "Lista atualizada." : "Consulta concluída.");
        } catch (error) {
            setError(error.message || "Não foi possível carregar as solicitações.");
            setMessage("Falha ao consultar a API.", true);
        } finally {
            setBusy(false);
        }
    }

    function addAction(container, text, className, handler) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = className;
        button.textContent = text;
        button.addEventListener("click", handler);
        container.append(button);
    }

    function addHistory(entries) {
        const list = document.getElementById("request-history");
        const empty = document.getElementById("request-history-empty");
        list.replaceChildren();
        const history = Array.isArray(entries) ? entries : [];
        empty.classList.toggle("d-none", history.length > 0);
        for (const entry of history) {
            const item = document.createElement("li");
            item.className = "list-group-item";
            const heading = document.createElement("div");
            heading.className = "small text-secondary";
            heading.textContent = `${formatDate(entry.created_at)}${entry.user_id ? ` · usuário ${entry.user_id}` : ""}`;
            const transition = document.createElement("div");
            transition.className = "fw-semibold";
            transition.textContent = `${entry.from ? `${statusLabel(entry.from)} → ` : ""}${statusLabel(entry.to)}`;
            item.append(heading, transition);
            if (entry.comment) {
                const comment = document.createElement("div");
                comment.textContent = entry.comment;
                item.append(comment);
            }
            list.append(item);
        }
    }

    function renderApprovalItems(items) {
        const container = document.getElementById("request-approval-items");
        container.replaceChildren();
        for (const item of items) {
            const row = document.createElement("div");
            row.className = "row g-2 align-items-center mb-2";
            const label = document.createElement("label");
            label.className = "col form-label mb-0";
            label.textContent = `${item.code} — ${item.name} (solicitado: ${item.qty_requested})`;
            const input = document.createElement("input");
            input.className = "form-control col-auto";
            input.type = "number";
            input.min = "0";
            input.max = String(item.qty_requested);
            input.value = String(item.qty_approved ?? item.qty_requested);
            input.dataset.itemId = item.item_id;
            input.setAttribute("aria-label", `Quantidade aprovada para ${item.name}`);
            input.style.maxWidth = "9rem";
            row.append(label, input);
            container.append(row);
        }
    }

    function renderDetail(request) {
        currentRequest = request;
        document.getElementById("request-detail-code").textContent = request.code || "";
        document.getElementById("request-detail-title").textContent = request.purpose || "Solicitação";
        const status = document.getElementById("request-detail-status");
        status.textContent = statusLabel(request.status);
        status.className = "badge text-bg-secondary";
        document.getElementById("request-requester").textContent = request.requester_id === user?.id
            ? `${user.name || "Você"} (você)`
            : request.requester_name || request.requester_id || "—";
        document.getElementById("request-department").textContent = request.department_name || request.department_id || "—";
        document.getElementById("request-industry").textContent = request.industry_name || request.industry_id || "—";
        document.getElementById("request-recipient").textContent = request.recipient || "—";
        document.getElementById("request-needed-date").textContent = formatDateOnly(request.needed_date);
        document.getElementById("request-total-value").textContent = Number.isFinite(Number(request.total_value))
            ? new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(Number(request.total_value))
            : "—";
        document.getElementById("request-needs-purchase").textContent = request.needs_purchase ? "Requer compra" : "Não";
        document.getElementById("request-purchase-ticket").textContent = request.purchase_ticket_no || "—";
        document.getElementById("request-notes").textContent = request.notes || "—";
        document.getElementById("request-cancel-reason").textContent = request.cancel_reason || "—";

        const itemsContainer = document.getElementById("request-items");
        itemsContainer.replaceChildren();
        const items = Array.isArray(request.items) ? request.items : [];
        for (const item of items) {
            const row = document.createElement("tr");
            appendCell(row, `${item.code} — ${item.name}`);
            appendCell(row, item.qty_requested);
            appendCell(row, item.qty_approved ?? "—");
            appendCell(row, item.qty_reserved);
            appendCell(row, item.qty_delivered);
            itemsContainer.append(row);
        }
        addHistory(request.history);

        const actions = document.getElementById("request-actions");
        actions.replaceChildren();
        const approvalPanel = document.getElementById("request-approval-panel");
        approvalPanel.classList.add("d-none");
        if (request.status === "rascunho" && String(request.requester_id) === String(user?.id) && permissions.includes("requests.create")) {
            const editLink = document.createElement("a");
            editLink.className = "btn btn-outline-primary";
            editLink.href = `/requests/${encodeURIComponent(request.id)}/edit`;
            editLink.textContent = "Editar rascunho";
            actions.append(editLink);
            addAction(actions, "Enviar para processamento", "btn btn-primary", async () => {
                if (!window.confirm("Enviar esta solicitação para avaliação?")) return;
                await mutate(`/api/v1/requests/${encodeURIComponent(request.id)}/submit`, {}, "Solicitação enviada.");
            });
        }
        if (["rascunho", "aguardando_aprovacao", "solicitada"].includes(request.status)
            && (request.requester_id === user?.id || permissions.includes("requests.cancel_any") || isAdmin)) {
            addAction(actions, "Cancelar", "btn btn-outline-danger", async () => {
                const reason = window.prompt("Motivo do cancelamento (opcional):");
                if (reason === null || !window.confirm("Confirmar o cancelamento da solicitação?")) return;
                await mutate(`/api/v1/requests/${encodeURIComponent(request.id)}/cancel`, { reason }, "Solicitação cancelada.");
            });
        }
        if (request.status === "aguardando_aprovacao" && (permissions.includes("requests.approve") || isAdmin)) {
            renderApprovalItems(items);
            addAction(actions, "Decidir aprovação", "btn btn-primary", () => {
                approvalPanel.classList.remove("d-none");
                approvalPanel.scrollIntoView({ behavior: "smooth", block: "center" });
                document.getElementById("request-justification").focus();
            });
        }
        if (request.status === "aprovada" && (permissions.includes("requests.process") || isAdmin)) {
            addAction(actions, "Iniciar separação", "btn btn-primary", async () => {
                if (!window.confirm("Iniciar a separação desta solicitação?")) return;
                await mutate(`/api/v1/requests/${encodeURIComponent(request.id)}/start-picking`, undefined, "Separação iniciada.");
            });
        }
        if (request.status === "em_separacao" && (permissions.includes("requests.process") || isAdmin)) {
            addAction(actions, "Marcar como pronta", "btn btn-primary", async () => {
                if (!window.confirm("Marcar a solicitação como pronta para retirada?")) return;
                await mutate(`/api/v1/requests/${encodeURIComponent(request.id)}/ready`, undefined, "Solicitação marcada como pronta.");
            });
        }
        if (request.status === "pronta" && (permissions.includes("requests.process") || isAdmin)) {
            const link = document.createElement("a");
            link.className = "btn btn-primary";
            link.href = `/operacao/${encodeURIComponent(request.id)}/entrega`;
            link.textContent = "Registrar entrega";
            actions.append(link);
        }
    }

    async function loadDetail() {
        if (!token || busy) return;
        setError("");
        setBusy(true);
        setMessage("Carregando detalhes…");
        try {
            const response = await api(`/api/v1/requests/${encodeURIComponent(requestId)}`);
            const request = response.data || response;
            if (!request || typeof request !== "object") throw new Error("A API não retornou a solicitação.");
            renderDetail(request);
            setMessage("Detalhes atualizados.");
        } catch (error) {
            setError(error.message || "Não foi possível carregar a solicitação.");
            setMessage("Falha ao consultar a API.", true);
        } finally {
            setBusy(false);
        }
    }

    async function mutate(path, body, successMessage) {
        if (busy || !token) return;
        setError("");
        setBusy(true);
        setMessage("Salvando transição…");
        try {
            await api(path, { method: "POST", ...(body === undefined ? {} : { body }) });
        } catch (error) {
            setError(error.message || "A API rejeitou a transição.");
            setMessage("A transição não foi confirmada.", true);
            setBusy(false);
            return;
        }
        setMessage(successMessage);
        try {
            if (isDetail) await loadDetailAfterMutation();
            else await loadListAfterMutation();
        } catch {
            setMessage(`${successMessage} Não foi possível atualizar a tela; recarregue os dados.`, true);
        } finally {
            setBusy(false);
        }
    }

    async function loadDetailAfterMutation() {
        const response = await api(`/api/v1/requests/${encodeURIComponent(requestId)}`);
        renderDetail(response.data || response);
    }

    async function loadListAfterMutation() {
        const response = await api(lastListUrl);
        const rows = Array.isArray(response.data) ? response.data : [];
        renderRows(rows);
        renderCounts(response.meta?.counts);
    }

    async function decide(approved) {
        const justificationInput = document.getElementById("request-justification");
        if (!justificationInput.reportValidity()) return;
        const justification = justificationInput.value.trim();
        const request = currentRequest;
        if (!request) return;
        const body = { justification };
        if (approved) {
            body.items = [...document.querySelectorAll("#request-approval-items input[data-item-id]")].map(input => ({
                item_id: input.dataset.itemId,
                qty_approved: Number(input.value)
            }));
        }
        await mutate(`/api/v1/requests/${encodeURIComponent(request.id)}/${approved ? "approve" : "reject"}`, body,
            approved ? "Aprovação registrada." : "Reprovação registrada.");
        justificationInput.value = "";
    }

    loginForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !loginForm.reportValidity()) return;
        setError("");
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
            token = data.accessToken || null;
            if (data.mustChangePassword || data.must_change_password)
                throw new Error("Esta conta precisa trocar a senha antes de usar solicitações.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            user = data.user || null;
            permissions = Array.isArray(data.permissions) ? data.permissions : [];
            isAdmin = Boolean(data.isAdmin ?? data.is_admin);
            passwordInput.value = "";
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${user?.name || user?.email || "usuário"}.`;
            document.getElementById("requests-new").classList.toggle("d-none", !permissions.includes("requests.create"));
            setBusy(false);
            if (isDetail) {
                listPanel.classList.add("d-none");
                detailPanel.classList.remove("d-none");
                await loadDetail();
            } else {
                detailPanel.classList.add("d-none");
                listPanel.classList.remove("d-none");
                await loadList();
            }
        } catch (error) {
            if (token) await fetch("/api/v1/auth/logout", { method: "POST", cache: "no-store", headers: { Authorization: `Bearer ${token}`, Accept: "application/json" } }).catch(() => {});
            clearSession();
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
            // A sessão local é descartada mesmo que o servidor esteja indisponível.
        } finally {
            clearSession();
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });

    retryButton.addEventListener("click", () => isDetail ? loadDetail() : loadList());
    refreshButton?.addEventListener("click", () => loadList());
    filterButton?.addEventListener("click", () => { pageNumber = 1; loadList(); });
    statusSelect?.addEventListener("change", () => { pageNumber = 1; loadList(); });
    fromInput?.addEventListener("change", () => { pageNumber = 1; loadList(); });
    toInput?.addEventListener("change", () => { pageNumber = 1; loadList(); });
    searchInput?.addEventListener("input", () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => { pageNumber = 1; loadList(); }, 300);
    });
    previousButton?.addEventListener("click", () => {
        if (pageNumber > 1) { pageNumber -= 1; loadList(); }
    });
    nextButton?.addEventListener("click", () => {
        pageNumber += 1;
        loadList();
    });
    document.getElementById("request-approve")?.addEventListener("click", () => decide(true));
    document.getElementById("request-reject")?.addEventListener("click", () => decide(false));
    document.getElementById("request-approval-close")?.addEventListener("click", () => {
        document.getElementById("request-approval-panel").classList.add("d-none");
    });
    if (isDetail) configureBackLink();
})();
