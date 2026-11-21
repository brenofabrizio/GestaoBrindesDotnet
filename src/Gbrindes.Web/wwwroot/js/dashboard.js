(() => {
    const page = document.getElementById("dashboard-page");
    if (!page) return;

    const loginForm = document.getElementById("dashboard-login-form");
    const emailInput = document.getElementById("dashboard-email");
    const passwordInput = document.getElementById("dashboard-password");
    const loginButton = document.getElementById("dashboard-login");
    const sessionPanel = document.getElementById("dashboard-session");
    const userLabel = document.getElementById("dashboard-user-label");
    const logoutButton = document.getElementById("dashboard-logout");
    const summary = document.getElementById("dashboard-summary");
    const filterForm = document.getElementById("dashboard-filters");
    const presetSelect = document.getElementById("dashboard-preset");
    const fromInput = document.getElementById("dashboard-from");
    const toInput = document.getElementById("dashboard-to");
    const refreshButton = document.getElementById("dashboard-refresh");
    const cards = document.getElementById("dashboard-cards");
    const alerts = document.getElementById("dashboard-alerts");
    const message = document.getElementById("dashboard-message");
    const periodLabel = document.getElementById("dashboard-period-label");

    let token = null;
    let busy = false;
    let requestSequence = 0;
    let activeController = null;

    const localDate = date => {
        const year = date.getFullYear();
        const month = String(date.getMonth() + 1).padStart(2, "0");
        const day = String(date.getDate()).padStart(2, "0");
        return `${year}-${month}-${day}`;
    };
    const numberFormat = new Intl.NumberFormat("pt-BR");
    const moneyFormat = new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" });

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    async function responseData(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const fieldMessages = data.errors && Object.values(data.errors).flat().join(" ");
            throw new Error(fieldMessages || data.message || data.title || data.error || `Falha na requisição (${response.status}).`);
        }
        return data;
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        refreshButton.disabled = value;
        presetSelect.disabled = value;
        fromInput.disabled = value;
        toInput.disabled = value;
        logoutButton.disabled = value;
    }

    function addCard(label, value, hint = "") {
        const column = document.createElement("div");
        column.className = "col-6 col-lg-3";
        const card = document.createElement("div");
        card.className = "card card-body h-100";
        const title = document.createElement("div");
        title.className = "small text-secondary";
        title.textContent = label;
        const metric = document.createElement("div");
        metric.className = "fs-3 fw-semibold";
        metric.textContent = String(value);
        card.append(title, metric);
        if (hint) {
            const note = document.createElement("div");
            note.className = "small text-secondary";
            note.textContent = hint;
            card.append(note);
        }
        column.append(card);
        cards.append(column);
    }

    function render(data) {
        cards.replaceChildren();
        const stock = data.stock;
        const requests = data.requests;
        addCard("Itens ativos", numberFormat.format(stock.items_active));
        addCard("Unidades disponíveis", numberFormat.format(stock.units_available), `${numberFormat.format(stock.units_on_hand)} em estoque · ${numberFormat.format(stock.units_reserved)} reservadas`);
        if (stock.stock_value !== null && stock.stock_value !== undefined)
            addCard("Valor em estoque", moneyFormat.format(stock.stock_value));
        addCard("Abaixo do mínimo", numberFormat.format(stock.low_stock));
        addCard("Sem estoque", numberFormat.format(stock.zero_stock));
        addCard("Solicitações pendentes", numberFormat.format(requests.pending));
        addCard("Aguardando aprovação", numberFormat.format(requests.awaiting_approval));
        addCard("A preparar", numberFormat.format(requests.to_prepare));
        addCard("Prontas", numberFormat.format(requests.ready));
        addCard("Finalizadas no período", numberFormat.format(requests.finalized_period));

        const period = requests.period;
        periodLabel.textContent = `Solicitações finalizadas de ${period.from} a ${period.to}. Os demais indicadores refletem o estado atual.`;
        if (!Array.isArray(data.alerts) || data.alerts.length === 0) {
            const empty = document.createElement("p");
            empty.className = "text-secondary mb-0";
            empty.textContent = "Nenhum alerta de estoque.";
            alerts.replaceChildren(empty);
            return;
        }

        const table = document.createElement("table");
        table.className = "table table-sm table-striped align-middle mb-0";
        const head = document.createElement("thead");
        const header = document.createElement("tr");
        for (const label of ["Código", "Brinde", "Situação", "Disponível", "Mínimo"]) {
            const cell = document.createElement("th");
            cell.scope = "col";
            cell.textContent = label;
            header.append(cell);
        }
        head.append(header);
        const body = document.createElement("tbody");
        for (const alert of data.alerts) {
            const row = document.createElement("tr");
            for (const value of [alert.item?.code, alert.item?.name, alert.level_label, alert.available, alert.min_stock]) {
                const cell = document.createElement("td");
                cell.textContent = String(value ?? "—");
                row.append(cell);
            }
            body.append(row);
        }
        table.append(head, body);
        const wrapper = document.createElement("div");
        wrapper.append(table);
        alerts.replaceChildren(wrapper);
    }

    async function loadSummary() {
        if (!token || busy) return;
        if (!fromInput.value || !toInput.value) {
            setMessage("Informe as datas inicial e final.", true);
            return;
        }
        const sequence = ++requestSequence;
        activeController?.abort();
        activeController = new AbortController();
        setBusy(true);
        setMessage("Carregando indicadores…");
        try {
            const query = new URLSearchParams({ from: fromInput.value, to: toInput.value });
            const headers = { Authorization: `Bearer ${token}`, Accept: "application/json" };
            const options = { cache: "no-store", headers, signal: activeController.signal };
            const [stockResponse, requestResponse, alertsResponse] = await Promise.all([
                fetch(`/api/v1/dashboard/stock-summary?${query}`, options),
                fetch(`/api/v1/dashboard/request-summary?${query}`, options),
                fetch("/api/v1/dashboard/stock-alerts", options)
            ]);
            const [stock, requests, alertRows] = await Promise.all([
                responseData(stockResponse), responseData(requestResponse), responseData(alertsResponse)
            ]);
            if (sequence !== requestSequence || !token) return;
            render({ stock, requests, alerts: alertRows });
            setMessage("Indicadores atualizados.");
        } catch (error) {
            if (error.name !== "AbortError" && sequence === requestSequence)
                setMessage(error.message || "Não foi possível carregar o painel.", true);
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
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de usar o painel. Conclua a troca de senha e entre novamente.");
            if (!Array.isArray(data.permissions) || !data.permissions.includes("dashboard.view"))
                throw new Error("A conta não possui permissão para visualizar o painel.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            summary.classList.remove("d-none");
            const today = new Date();
            toInput.value = localDate(today);
            fromInput.value = localDate(new Date(today.getFullYear(), today.getMonth(), 1));
            setBusy(false);
            await loadSummary();
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
            cards.replaceChildren();
            alerts.replaceChildren();
            summary.classList.add("d-none");
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });

    filterForm.addEventListener("submit", event => {
        event.preventDefault();
        loadSummary();
    });

    presetSelect.addEventListener("change", () => {
        const today = new Date();
        toInput.value = localDate(today);
        if (presetSelect.value === "month") {
            fromInput.value = localDate(new Date(today.getFullYear(), today.getMonth(), 1));
        } else {
            const start = new Date(today);
            start.setDate(start.getDate() - Number(presetSelect.value) + 1);
            fromInput.value = localDate(start);
        }
    });
})();
