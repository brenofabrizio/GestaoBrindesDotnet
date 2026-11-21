(() => {
    const page = document.getElementById("item-import-page");
    if (!page) return;

    const loginForm = document.getElementById("import-login-form");
    const emailInput = document.getElementById("import-email");
    const passwordInput = document.getElementById("import-password");
    const loginButton = document.getElementById("import-login");
    const sessionPanel = document.getElementById("import-session");
    const userLabel = document.getElementById("import-user-label");
    const logoutButton = document.getElementById("import-logout");
    const workflow = document.getElementById("import-workflow");
    const fileInput = document.getElementById("import-file");
    const templateButton = page.querySelector("[data-import-template]");
    const previewButton = page.querySelector("[data-import-preview]");
    const applyButton = page.querySelector("[data-import-apply]");
    const results = document.getElementById("import-results");
    const message = document.getElementById("import-message");
    const typeSelect = page.querySelector("[data-import-type]");

    let token = null;
    let selectedFile = null;
    let previewFile = null;
    let previewRows = [];
    let busy = false;

    const endpoint = (kind, operation) => page.dataset[`import${operation[0].toUpperCase()}${operation.slice(1)}${kind[0].toUpperCase()}${kind.slice(1)}`];
    const selectedType = () => typeSelect.value;
    const setMessage = text => { message.textContent = text; };
    const setBusy = value => {
        busy = value;
        loginButton.disabled = value;
        templateButton.disabled = value || !token;
        fileInput.disabled = value || !token;
        previewButton.disabled = value || !token || !selectedFile;
        applyButton.disabled = value || !token || previewFile !== selectedFile || !previewRows.some(row => row.ok);
    };

    async function responseData(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const fieldMessages = data.errors && Object.values(data.errors).flat().join(" ");
            throw new Error(fieldMessages || data.message || data.title || data.error || `Falha na requisição (${response.status}).`);
        }
        return data;
    }

    function clearPreview() {
        previewFile = null;
        previewRows = [];
        results.replaceChildren();
        applyButton.disabled = true;
    }

    function showPreview(data) {
        previewRows = Array.isArray(data.rows) ? data.rows : [];
        previewFile = selectedFile;
        results.replaceChildren();

        const summary = document.createElement("p");
        summary.className = "fw-semibold";
        const report = data.report || {};
        summary.textContent = `${data.total ?? previewRows.length} linha(s); ${report.valid ?? (data.total - (data.errors ?? 0))} válida(s); ${report.invalid ?? (data.errors ?? 0)} com erro; ${report.pending ?? data.total ?? previewRows.length} aguardando confirmação.`;
        results.append(summary);

        const wrapper = document.createElement("div");
        wrapper.className = "table-responsive";
        const table = document.createElement("table");
        table.className = "table table-sm table-striped align-middle";
        const head = document.createElement("thead");
        const headerRow = document.createElement("tr");
        for (const label of ["Linha", "Resultado", "Reconciliação", "Dados reconhecidos", "Erros/avisos"]) {
            const th = document.createElement("th");
            th.scope = "col";
            th.textContent = label;
            headerRow.append(th);
        }
        head.append(headerRow);
        const body = document.createElement("tbody");
        for (const row of previewRows) {
            const tr = document.createElement("tr");
            const values = [
                row.line ?? "—",
                row.ok ? "Válida" : "Inválida",
                row.reconciliation ?? "Aguardando confirmação",
                row.displayName ?? row.preview ?? (row.payload ? JSON.stringify(row.payload) : "—"),
                Array.isArray(row.errors) && row.errors.length > 0 ? row.errors.join("; ") : "Pronta para importação"
            ];
            values.forEach((value, index) => {
                const td = document.createElement("td");
                td.textContent = String(value);
                if (index === 1) td.className = row.ok ? "text-success fw-semibold" : "text-danger fw-semibold";
                tr.append(td);
            });
            body.append(tr);
        }
        table.append(head, body);
        wrapper.append(table);
        results.append(wrapper);
        setMessage(previewRows.some(row => row.ok)
            ? "Prévia concluída. Confira as linhas; a confirmação importará somente as válidas."
            : "Prévia concluída sem linhas válidas para importar.");
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
            const data = await responseData(response);
            passwordInput.value = "";
            token = data.accessToken || null;
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de usar a API. Conclua a troca de senha e entre novamente.");
            if (!Array.isArray(data.permissions) || !data.permissions.includes("import.run"))
                throw new Error("A conta não possui a permissão necessária para importar brindes.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            workflow.classList.remove("d-none");
            setMessage("Acesso autorizado. Selecione um arquivo Excel para começar.");
        } catch (error) {
            if (token) {
                await fetch("/api/v1/auth/logout", {
                    method: "POST",
                    cache: "no-store",
                    headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
                }).catch(() => {});
            }
            token = null;
            setMessage(error.message || "Não foi possível entrar.");
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
            selectedFile = null;
            fileInput.value = "";
            clearPreview();
            workflow.classList.add("d-none");
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });

    typeSelect.addEventListener("change", () => {
        selectedFile = null;
        fileInput.value = "";
        clearPreview();
        setMessage(`Tipo selecionado: ${selectedType() === "requests" ? "solicitações" : "brindes"}. Selecione um arquivo Excel.`);
        setBusy(false);
    });

    fileInput.addEventListener("change", () => {
        selectedFile = fileInput.files?.[0] || null;
        clearPreview();
        setMessage(selectedFile ? "Arquivo selecionado. Gere a prévia antes de importar." : "");
        setBusy(false);
    });

    templateButton.addEventListener("click", async () => {
        if (!token || busy) return;
        setBusy(true);
        setMessage("Baixando modelo…");
        try {
            const response = await fetch(endpoint(selectedType(), "template"), {
                cache: "no-store",
                headers: { Authorization: `Bearer ${token}` }
            });
            if (!response.ok) await responseData(response);
            const blob = await response.blob();
            const url = URL.createObjectURL(blob);
            const link = document.createElement("a");
            link.href = url;
            link.download = "modelo-importacao-brindes.xlsx";
            link.click();
            URL.revokeObjectURL(url);
            setMessage("Modelo baixado.");
        } catch (error) {
            setMessage(error.message || "Não foi possível baixar o modelo.");
        } finally {
            setBusy(false);
        }
    });

    previewButton.addEventListener("click", async () => {
        if (!token || !selectedFile || busy) return;
        clearPreview();
        setBusy(true);
        setMessage("Validando a planilha sem gravar dados…");
        try {
            const form = new FormData();
            form.append("file", selectedFile, selectedFile.name);
            const response = await fetch(endpoint(selectedType(), "preview"), {
                method: "POST",
                cache: "no-store",
                headers: { Authorization: `Bearer ${token}`, Accept: "application/json" },
                body: form
            });
            showPreview(await responseData(response));
        } catch (error) {
            clearPreview();
            setMessage(error.message || "Não foi possível pré-visualizar a planilha.");
            setBusy(false);
        }
    });

    applyButton.addEventListener("click", async () => {
        if (!token || !selectedFile || previewFile !== selectedFile || !previewRows.some(row => row.ok) || busy) return;
        if (!window.confirm("Confirmar a importação das linhas válidas desta planilha? Esta ação grava os brindes no sistema.")) return;
        setBusy(true);
        setMessage("Importando linhas válidas…");
        try {
            const form = new FormData();
            form.append("file", selectedFile, selectedFile.name);
            const response = await fetch(endpoint(selectedType(), "apply"), {
                method: "POST",
                cache: "no-store",
                headers: { Authorization: `Bearer ${token}`, Accept: "application/json" },
                body: form
            });
            const data = await responseData(response);
            const result = data.result || data;
            clearPreview();
            fileInput.value = "";
            selectedFile = null;
            setMessage(`Importação concluída: ${result.created ?? 0} criado(s), ${result.skipped ?? 0} ignorado(s).`);
        } catch (error) {
            setMessage(error.message || "A importação falhou; confira o arquivo e tente novamente.");
        } finally {
            setBusy(false);
        }
    });
})();
