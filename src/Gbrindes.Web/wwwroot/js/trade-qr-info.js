(() => {
    const page = document.getElementById("trade-qr-info-page");
    if (!page) return;

    const loginForm = document.getElementById("trade-qr-info-login-form");
    const emailInput = document.getElementById("trade-qr-info-email");
    const passwordInput = document.getElementById("trade-qr-info-password");
    const loginButton = document.getElementById("trade-qr-info-login");
    const sessionPanel = document.getElementById("trade-qr-info-session");
    const userLabel = document.getElementById("trade-qr-info-user-label");
    const logoutButton = document.getElementById("trade-qr-info-logout");
    const codeForm = document.getElementById("trade-qr-info-code-form");
    const codeInput = document.getElementById("trade-qr-info-code");
    const lookupButton = document.getElementById("trade-qr-info-lookup");
    const message = document.getElementById("trade-qr-info-message");
    const results = document.getElementById("trade-qr-info-results");
    const resultCode = document.getElementById("trade-qr-info-results-title");
    const industry = document.getElementById("trade-qr-info-industry");
    const products = document.getElementById("trade-qr-info-products");

    let token = null;
    let busy = false;

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    async function responseData(response) {
        const body = await response.json().catch(() => ({}));
        if (!response.ok) {
            throw new Error(body.error?.message || body.message || body.title || `Falha na requisição (${response.status}).`);
        }
        return body;
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        lookupButton.disabled = value || !token;
        codeInput.disabled = value;
        logoutButton.disabled = value;
    }

    function render(data) {
        resultCode.textContent = String(data.code ?? "");
        industry.textContent = String(data.industry ?? "—");
        products.replaceChildren();
        for (const product of Array.isArray(data.products) ? data.products : []) {
            const row = document.createElement("tr");
            const name = document.createElement("td");
            const code = document.createElement("td");
            const balance = document.createElement("td");
            name.textContent = String(product.name ?? "—");
            code.textContent = String(product.code ?? "—");
            balance.className = "text-end";
            balance.textContent = Number(product.saldo ?? 0).toLocaleString("pt-BR");
            row.append(name, code, balance);
            products.append(row);
        }
        if (products.childElementCount === 0) {
            const row = document.createElement("tr");
            const empty = document.createElement("td");
            empty.colSpan = 3;
            empty.className = "text-secondary";
            empty.textContent = "Nenhum produto nesta solicitação.";
            row.append(empty);
            products.append(row);
        }
        results.classList.remove("d-none");
    }

    async function lookup() {
        if (!token || busy) return;
        const code = codeInput.value.trim();
        if (!code) {
            setMessage("Informe o código TRADE.", true);
            codeInput.focus();
            return;
        }

        setBusy(true);
        results.classList.add("d-none");
        setMessage("Consultando código TRADE…");
        try {
            const query = new URLSearchParams({ code });
            const response = await fetch(`/api/v1/trade-requests/qr-info?${query}`, {
                cache: "no-store",
                headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
            });
            const body = await responseData(response);
            render(body.data || {});
            setMessage("Consulta atualizada.");
        } catch (error) {
            setMessage(error.message || "Não foi possível consultar este código TRADE.", true);
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
            if (!data.accessToken) throw new Error("A autenticação não retornou uma sessão válida.");
            token = data.accessToken;
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de consultar o TRADE.");
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            lookupButton.disabled = false;
            setMessage("A API valida as permissões e o escopo da consulta.");
            if (codeInput.value.trim()) {
                setBusy(false);
                await lookup();
            }
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

    codeForm.addEventListener("submit", event => {
        event.preventDefault();
        lookup();
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
            results.classList.add("d-none");
            products.replaceChildren();
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });

    const initialCode = page.dataset.code?.trim();
    if (!codeInput.value.trim() && initialCode) codeInput.value = initialCode;
})();
