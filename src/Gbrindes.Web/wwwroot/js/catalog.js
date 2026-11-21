(() => {
    const page = document.getElementById("catalog-page");
    if (!page) return;

    const loginForm = document.getElementById("catalog-login-form");
    const emailInput = document.getElementById("catalog-email");
    const passwordInput = document.getElementById("catalog-password");
    const loginButton = document.getElementById("catalog-login");
    const sessionPanel = document.getElementById("catalog-session");
    const userLabel = document.getElementById("catalog-user-label");
    const logoutButton = document.getElementById("catalog-logout");
    const results = document.getElementById("catalog-results");
    const filterForm = document.getElementById("catalog-filters");
    const searchInput = document.getElementById("catalog-search");
    const statusSelect = document.getElementById("catalog-status");
    const refreshButton = document.getElementById("catalog-refresh");
    const tableContainer = document.getElementById("catalog-table");
    const message = document.getElementById("catalog-message");
    const previousButton = document.getElementById("catalog-previous");
    const nextButton = document.getElementById("catalog-next");
    const pageLabel = document.getElementById("catalog-page-label");

    let token = null;
    let pageNumber = 1;
    let metadata = null;
    let debounceTimer = null;
    let activeController = null;
    let requestSequence = 0;
    let busy = false;
    const numberFormat = new Intl.NumberFormat("pt-BR");
    const moneyFormat = new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" });

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        searchInput.disabled = value;
        statusSelect.disabled = value;
        refreshButton.disabled = value;
        previousButton.disabled = value || !metadata || metadata.page <= 1;
        nextButton.disabled = value || !metadata || metadata.page >= metadata.last_page;
        logoutButton.disabled = value;
    }

    async function responseData(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const fieldMessages = data.errors && Object.values(data.errors).flat().join(" ");
            throw new Error(fieldMessages || data.message || data.title || data.error || `Falha na requisição (${response.status}).`);
        }
        return data;
    }

    function cell(value, className = "") {
        const element = document.createElement("td");
        element.textContent = String(value ?? "—");
        if (className) element.className = className;
        return element;
    }

    function renderRows(rows) {
        tableContainer.replaceChildren();
        if (!rows.length) {
            const empty = document.createElement("p");
            empty.className = "card card-body text-secondary mb-0";
            empty.textContent = "Nenhum brinde encontrado. Ajuste os filtros e tente novamente.";
            tableContainer.append(empty);
            return;
        }

        const table = document.createElement("table");
        table.className = "table table-hover align-middle mb-0";
        const head = document.createElement("thead");
        head.className = "table-light";
        const header = document.createElement("tr");
        for (const label of ["Código", "Brinde", "Categoria", "Indústria", "Local", "Status", "Estoque mínimo", "Valor unitário"]) {
            const th = document.createElement("th");
            th.scope = "col";
            th.textContent = label;
            header.append(th);
        }
        head.append(header);
        const body = document.createElement("tbody");
        for (const item of rows) {
            const row = document.createElement("tr");
            const nameCell = document.createElement("td");
            nameCell.className = "fw-semibold";
            if (typeof item.id === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(item.id)) {
                const link = document.createElement("a");
                link.href = `/items/${encodeURIComponent(item.id)}`;
                link.textContent = item.name || "Brinde sem nome";
                nameCell.append(link);
            } else {
                nameCell.textContent = item.name || "Brinde sem nome";
            }
            row.append(
                cell(item.code, "font-monospace"),
                nameCell,
                cell(item.category?.name),
                cell(item.industry?.name),
                cell(item.location?.name),
                cell(item.status === "ativo" ? "Ativo" : item.status === "inativo" ? "Inativo" : item.status),
                cell(numberFormat.format(item.minStock ?? 0), "text-end"),
                cell(item.unitValue == null ? "—" : moneyFormat.format(item.unitValue), "text-end")
            );
            body.append(row);
        }
        table.append(head, body);
        tableContainer.append(table);
    }

    async function loadItems() {
        if (!token || busy) return;
        const sequence = ++requestSequence;
        activeController?.abort();
        activeController = new AbortController();
        setBusy(true);
        setMessage("Carregando catálogo…");
        try {
            const query = new URLSearchParams({ page: String(pageNumber), per_page: "25" });
            if (searchInput.value.trim()) query.set("q", searchInput.value.trim());
            if (statusSelect.value) query.set("status", statusSelect.value);
            const response = await fetch(`/api/v1/items?${query}`, {
                cache: "no-store",
                headers: { Authorization: `Bearer ${token}`, Accept: "application/json" },
                signal: activeController.signal
            });
            const data = await responseData(response);
            if (sequence !== requestSequence || !token) return;
            metadata = data.meta;
            renderRows(Array.isArray(data.data) ? data.data : []);
            pageLabel.textContent = `Página ${metadata.page} de ${metadata.last_page} · ${numberFormat.format(metadata.total)} brindes`;
            setMessage("Catálogo atualizado.");
        } catch (error) {
            if (error.name !== "AbortError" && sequence === requestSequence)
                setMessage(error.message || "Não foi possível carregar o catálogo.", true);
        } finally {
            if (sequence === requestSequence) setBusy(false);
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
                throw new Error("A conta não possui permissão para consultar o catálogo.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            results.classList.remove("d-none");
            setBusy(false);
            await loadItems();
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
            requestSequence++;
            activeController?.abort();
            metadata = null;
            tableContainer.replaceChildren();
            pageLabel.textContent = "";
            results.classList.add("d-none");
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });

    filterForm.addEventListener("submit", event => {
        event.preventDefault();
        if (busy) return;
        pageNumber = 1;
        loadItems();
    });

    searchInput.addEventListener("input", () => {
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
            if (busy) return;
            pageNumber = 1;
            loadItems();
        }, 300);
    });

    statusSelect.addEventListener("change", () => {
        if (busy) return;
        pageNumber = 1;
        loadItems();
    });

    previousButton.addEventListener("click", () => {
        if (busy || !metadata || metadata.page <= 1) return;
        pageNumber = metadata.page - 1;
        loadItems();
    });

    nextButton.addEventListener("click", () => {
        if (busy || !metadata || metadata.page >= metadata.last_page) return;
        pageNumber = metadata.page + 1;
        loadItems();
    });
})();
