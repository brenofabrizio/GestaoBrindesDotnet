(() => {
    const page = document.getElementById("users-page");
    if (!page) return;

    const loginForm = document.getElementById("users-login-form");
    const emailInput = document.getElementById("users-email");
    const passwordInput = document.getElementById("users-password");
    const loginButton = document.getElementById("users-login");
    const sessionPanel = document.getElementById("users-session");
    const userLabel = document.getElementById("users-user-label");
    const logoutButton = document.getElementById("users-logout");
    const results = document.getElementById("users-results");
    const filters = document.getElementById("users-filters");
    const searchInput = document.getElementById("users-search");
    const activeSelect = document.getElementById("users-active");
    const trashedInput = document.getElementById("users-trashed");
    const refreshButton = document.getElementById("users-refresh");
    const message = document.getElementById("users-message");
    const tableContainer = document.getElementById("users-table");
    const previousButton = document.getElementById("users-previous");
    const nextButton = document.getElementById("users-next");
    const pageLabel = document.getElementById("users-page-label");
    const createPanel = document.getElementById("users-create-panel");
    const createForm = document.getElementById("users-create-form");
    const createNameInput = document.getElementById("users-create-name");
    const createEmailInput = document.getElementById("users-create-email");
    const createPasswordInput = document.getElementById("users-create-password");
    const createRoleSelect = document.getElementById("users-create-role");
    const createButton = document.getElementById("users-create-button");
    const editPanel = document.getElementById("users-edit-panel");
    const editForm = document.getElementById("users-edit-form");
    const editIdInput = document.getElementById("users-edit-id");
    const editNameInput = document.getElementById("users-edit-name");
    const editEmailInput = document.getElementById("users-edit-email");
    const editPhoneInput = document.getElementById("users-edit-phone");
    const editDepartmentSelect = document.getElementById("users-edit-department");
    const editIndustrySelect = document.getElementById("users-edit-industry");
    const editRoleSelect = document.getElementById("users-edit-role");
    const editRoleNotice = document.getElementById("users-edit-role-notice");
    const editButton = document.getElementById("users-edit-button");
    const editCancelButton = document.getElementById("users-edit-cancel");
    const editPasswordForm = document.getElementById("users-edit-password-form");
    const editPasswordInput = document.getElementById("users-edit-password");
    const editPasswordButton = document.getElementById("users-edit-password-button");

    let token = null;
    let pageNumber = 1;
    let metadata = null;
    let busy = false;
    let requestSequence = 0;
    let canViewUsers = false;
    let canManageUsers = false;
    let roleOptionsLoaded = false;
    let editRoleIds = [];
    const numberFormat = new Intl.NumberFormat("pt-BR");
    const dateFormat = new Intl.DateTimeFormat("pt-BR", { dateStyle: "short", timeStyle: "short" });

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        searchInput.disabled = value;
        activeSelect.disabled = value;
        trashedInput.disabled = value;
        refreshButton.disabled = value;
        logoutButton.disabled = value;
        createNameInput.disabled = value;
        createEmailInput.disabled = value;
        createPasswordInput.disabled = value;
        createRoleSelect.disabled = value || !roleOptionsLoaded;
        createButton.disabled = value || !roleOptionsLoaded;
        editNameInput.disabled = value;
        editEmailInput.disabled = value;
        editPhoneInput.disabled = value;
        editDepartmentSelect.disabled = value || !roleOptionsLoaded;
        editIndustrySelect.disabled = value || !roleOptionsLoaded;
        editRoleSelect.disabled = value || !roleOptionsLoaded || editRoleIds.length > 1;
        editButton.disabled = value;
        editCancelButton.disabled = value;
        editPasswordInput.disabled = value;
        editPasswordButton.disabled = value;
        previousButton.disabled = value || !metadata || metadata.page <= 1;
        nextButton.disabled = value || !metadata || metadata.page >= metadata.last_page;
    }

    async function responseData(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const fieldMessages = data.errors && Object.values(data.errors).flat().join(" ");
            throw new Error(fieldMessages || data.detail || data.message || data.title || data.error || `Falha na requisição (${response.status}).`);
        }
        return data;
    }

    function cell(value, className = "") {
        const td = document.createElement("td");
        td.textContent = value == null || value === "" ? "—" : String(value);
        if (className) td.className = className;
        return td;
    }

    function formatDate(value) {
        if (!value) return "—";
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? "—" : dateFormat.format(date);
    }

    function renderRows(rows) {
        tableContainer.replaceChildren();
        if (!rows.length) {
            const empty = document.createElement("p");
            empty.className = "card card-body text-secondary mb-0";
            empty.textContent = "Nenhum usuário encontrado para os filtros selecionados.";
            tableContainer.append(empty);
            return;
        }

        const table = document.createElement("table");
        table.className = "table table-hover align-middle mb-0";
        const head = document.createElement("thead");
        head.className = "table-light";
        const headRow = document.createElement("tr");
        const labels = ["Nome", "E-mail", "Perfil(is)", "Departamento", "Indústria", "Situação", "Último acesso"];
        if (canManageUsers) labels.push("Ações");
        for (const label of labels) {
            const th = document.createElement("th");
            th.scope = "col";
            th.textContent = label;
            headRow.append(th);
        }
        head.append(headRow);
        const body = document.createElement("tbody");
        for (const user of rows) {
            const roleNames = Array.isArray(user.roles) ? user.roles.map(role => role.name).filter(Boolean).join(", ") : "";
            const status = user.deleted_at ? "Na lixeira" : user.active ? "Ativo" : "Inativo";
            const row = document.createElement("tr");
            row.append(
                cell(user.name, "fw-semibold"),
                cell(user.email),
                cell(roleNames),
                cell(user.department?.name),
                cell(user.industry?.name),
                cell(status),
                cell(formatDate(user.last_login_at))
            );
            if (canManageUsers) {
                const actions = document.createElement("td");
                actions.className = "text-nowrap";
                if (user.deleted_at) {
                    const restoreAction = document.createElement("button");
                    restoreAction.type = "button";
                    restoreAction.className = "btn btn-sm btn-outline-success";
                    restoreAction.textContent = "Restaurar";
                    restoreAction.dataset.action = "restore";
                    restoreAction.dataset.userId = user.id;
                    restoreAction.setAttribute("aria-label", `Restaurar ${user.name || "usuário"}`);
                    restoreAction.addEventListener("click", () => changeTrash(user));
                    actions.append(restoreAction);

                    const purgeAction = document.createElement("button");
                    purgeAction.type = "button";
                    purgeAction.className = "btn btn-sm btn-outline-danger ms-2";
                    purgeAction.textContent = "Excluir definitivamente";
                    purgeAction.dataset.action = "purge";
                    purgeAction.dataset.userId = user.id;
                    purgeAction.setAttribute("aria-label", `Excluir permanentemente ${user.name || "usuário"}`);
                    purgeAction.addEventListener("click", () => purgeUser(user));
                    actions.append(purgeAction);
                } else {
                    const editAction = document.createElement("button");
                    editAction.type = "button";
                    editAction.className = "btn btn-sm btn-outline-primary me-2";
                    editAction.textContent = "Editar";
                    editAction.dataset.action = "edit";
                    editAction.dataset.userId = user.id;
                    editAction.setAttribute("aria-label", `Editar ${user.name || "usuário"}`);
                    editAction.addEventListener("click", () => beginEdit(user));
                    actions.append(editAction);

                    const activeAction = document.createElement("button");
                    activeAction.type = "button";
                    activeAction.className = user.active ? "btn btn-sm btn-outline-danger" : "btn btn-sm btn-outline-success";
                    activeAction.textContent = user.active ? "Desativar" : "Ativar";
                    activeAction.dataset.action = user.active ? "deactivate" : "activate";
                    activeAction.dataset.userId = user.id;
                    activeAction.setAttribute("aria-label", `${user.active ? "Desativar" : "Ativar"} ${user.name || "usuário"}`);
                    activeAction.addEventListener("click", () => changeActive(user));
                    actions.append(activeAction);

                    const trashAction = document.createElement("button");
                    trashAction.type = "button";
                    trashAction.className = "btn btn-sm btn-outline-danger ms-2";
                    trashAction.textContent = "Enviar à lixeira";
                    trashAction.dataset.action = "trash";
                    trashAction.dataset.userId = user.id;
                    trashAction.setAttribute("aria-label", `Enviar ${user.name || "usuário"} à lixeira`);
                    trashAction.addEventListener("click", () => changeTrash(user));
                    actions.append(trashAction);
                }
                row.append(actions);
            }
            body.append(row);
        }
        table.append(head, body);
        tableContainer.append(table);
    }

    function beginEdit(user) {
        if (!canManageUsers || !user || user.deleted_at) return;
        const assignedRoles = Array.isArray(user.roles) ? user.roles : [];
        editRoleIds = assignedRoles.map(role => role.id).filter(Boolean);
        editIdInput.value = user.id;
        editNameInput.value = user.name || "";
        editEmailInput.value = user.email || "";
        editPhoneInput.value = user.phone || "";
        editDepartmentSelect.value = user.department?.id || "";
        editIndustrySelect.value = user.industry?.id || "";
        editPasswordInput.value = "";
        editRoleSelect.value = "";
        editRoleNotice.textContent = editRoleIds.length > 1
            ? `Este usuário possui múltiplos perfis (${assignedRoles.map(role => role.name).filter(Boolean).join(", ")}). A troca individual fica bloqueada para preservar todos os vínculos.`
            : editRoleIds.length === 1
                ? `Perfil atual: ${assignedRoles[0].name || "não identificado"}. Selecione outro perfil apenas se quiser substituí-lo.`
                : "Este usuário não possui perfil vinculado. Selecione um perfil somente se desejar atribuí-lo.";
        setBusy(busy);
        editPanel.classList.remove("d-none");
        editNameInput.focus();
    }

    function closeEdit() {
        editIdInput.value = "";
        editNameInput.value = "";
        editEmailInput.value = "";
        editPhoneInput.value = "";
        editDepartmentSelect.value = "";
        editIndustrySelect.value = "";
        editPasswordInput.value = "";
        editRoleSelect.value = "";
        editRoleNotice.textContent = "";
        editRoleIds = [];
        editPanel.classList.add("d-none");
    }

    async function changeActive(user) {
        if (!token || !canManageUsers || busy || user.deleted_at) return;
        const activating = !user.active;
        if (!activating && !globalThis.confirm(`Desativar ${user.name || "este usuário"}? As sessões atuais serão invalidadas.`)) return;
        await mutateUser(`/api/v1/users/${encodeURIComponent(user.id)}/${activating ? "activate" : "deactivate"}`,
            activating ? "Ativando usuário…" : "Desativando usuário…",
            activating ? "Usuário ativado." : "Usuário desativado.");
    }

    async function changeTrash(user) {
        if (!token || !canManageUsers || busy) return;
        const restoring = Boolean(user.deleted_at);
        const action = restoring ? "restaurar" : "enviar à lixeira";
        if (!globalThis.confirm(`Deseja ${action} ${user.name || "este usuário"}?`)) return;
        await mutateUser(`/api/v1/users/${encodeURIComponent(user.id)}${restoring ? "/restore" : ""}`,
            restoring ? "Restaurando usuário…" : "Enviando usuário à lixeira…",
            restoring ? "Usuário restaurado." : "Usuário enviado à lixeira.",
            restoring ? "POST" : "DELETE");
    }

    async function purgeUser(user) {
        if (!token || !canManageUsers || busy || !user.deleted_at) return;
        const email = String(user.email || "").trim();
        if (!email) {
            setMessage("Não é possível confirmar a exclusão sem o e-mail do usuário.", true);
            return;
        }
        const typedEmail = globalThis.prompt(`A exclusão é permanente e não pode ser desfeita. Digite o e-mail ${email} para continuar.`);
        if (typedEmail === null) return;
        if (typedEmail.trim().toLowerCase() !== email.toLowerCase()) {
            setMessage("O e-mail digitado não é igual ao e-mail do usuário.", true);
            return;
        }
        if (!globalThis.confirm(`Excluir permanentemente ${email}? Registros com histórico ou referências serão preservados e bloquearão a operação.`)) return;

        const completed = await mutateUser(`/api/v1/users/${encodeURIComponent(user.id)}/purge`,
            "Excluindo usuário permanentemente…", "Usuário excluído permanentemente.", "POST", { confirm: typedEmail.trim() });
        if (completed) setMessage("Usuário excluído permanentemente.");
    }

    async function mutateUser(url, progressMessage, successMessage, method = "POST", body = null) {
        if (!token || !canManageUsers || busy) return false;
        setBusy(true);
        setMessage(progressMessage);
        try {
            const response = await fetch(url, {
                method,
                cache: "no-store",
                headers: {
                    Authorization: `Bearer ${token}`,
                    Accept: "application/json",
                    ...(body === null ? {} : { "Content-Type": "application/json" })
                },
                ...(body === null ? {} : { body: JSON.stringify(body) })
            });
            await responseData(response);
            setBusy(false);
            await loadUsers();
            setMessage(successMessage);
            return true;
        } catch (error) {
            setMessage(error.message || "Não foi possível atualizar a situação do usuário.", true);
            return false;
        } finally {
            setBusy(false);
        }
    }

    async function loadUsers() {
        if (!token || busy) return;
        const sequence = ++requestSequence;
        setBusy(true);
        setMessage("Carregando diretório…");
        try {
            const query = new URLSearchParams({ page: String(pageNumber), per_page: "25" });
            if (searchInput.value.trim()) query.set("q", searchInput.value.trim());
            if (activeSelect.value) query.set("active", activeSelect.value);
            if (trashedInput.checked) query.set("trashed", "1");
            const response = await fetch(`/api/v1/users?${query}`, {
                cache: "no-store",
                headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
            });
            const data = await responseData(response);
            if (sequence !== requestSequence || !token) return;
            metadata = data.meta;
            renderRows(Array.isArray(data.data) ? data.data : []);
            pageLabel.textContent = `Página ${numberFormat.format(metadata.page)} de ${numberFormat.format(metadata.last_page)} · ${numberFormat.format(metadata.total)} usuários`;
            setMessage("Diretório atualizado.");
        } catch (error) {
            if (sequence === requestSequence) setMessage(error.message || "Não foi possível carregar os usuários.", true);
        } finally {
            if (sequence === requestSequence) setBusy(false);
        }
    }

    function resetRoleOptions() {
        createRoleSelect.replaceChildren();
        const placeholder = document.createElement("option");
        placeholder.value = "";
        placeholder.textContent = "Selecione um perfil";
        createRoleSelect.append(placeholder);
        editRoleSelect.replaceChildren();
        const preserveRole = document.createElement("option");
        preserveRole.value = "";
        preserveRole.textContent = "Manter perfil atual";
        editRoleSelect.append(preserveRole);
    }

    async function loadRoleOptions() {
        if (!token || busy) return;
        setBusy(true);
        setMessage("Carregando perfis disponíveis…");
        try {
            const response = await fetch("/api/v1/users/create-options", {
                cache: "no-store",
                headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
            });
            const data = await responseData(response);
            resetRoleOptions();
            const roles = Array.isArray(data.data) ? data.data : [];
            for (const role of roles) {
                for (const select of [createRoleSelect, editRoleSelect]) {
                    const option = document.createElement("option");
                    option.value = role.id;
                    option.textContent = role.name || role.slug || "Perfil";
                    select.append(option);
                }
            }
            for (const [select, options, emptyLabel] of [
                [editDepartmentSelect, data.departments, "Sem departamento"],
                [editIndustrySelect, data.industries, "Sem indústria"]
            ]) {
                select.replaceChildren();
                const empty = document.createElement("option");
                empty.value = "";
                empty.textContent = emptyLabel;
                select.append(empty);
                for (const item of Array.isArray(options) ? options : []) {
                    const option = document.createElement("option");
                    option.value = item.id;
                    option.textContent = item.active === false ? `${item.name || ""} (inativo)` : item.name || "";
                    select.append(option);
                }
            }
            roleOptionsLoaded = roles.length > 0;
            setMessage(roleOptionsLoaded ? "Perfis carregados para seleção explícita." : "Nenhum perfil disponível para cadastro.", !roleOptionsLoaded);
        } catch (error) {
            roleOptionsLoaded = false;
            resetRoleOptions();
            setMessage(error.message || "Não foi possível carregar os perfis disponíveis.", true);
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
            token = data.accessToken || null;
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de consultar usuários. Acesse /account/password.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            const permissions = new Set(Array.isArray(data.permissions) ? data.permissions : []);
            canViewUsers = data.isAdmin === true || permissions.has("users.view");
            canManageUsers = data.isAdmin === true || permissions.has("users.manage");
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            results.classList.toggle("d-none", !canViewUsers);
            createPanel.classList.toggle("d-none", !canManageUsers);
            setBusy(false);
            if (canManageUsers) await loadRoleOptions();
            if (canViewUsers) await loadUsers();
            if (!canViewUsers && !canManageUsers) setMessage("Sua conta não tem permissão para consultar ou cadastrar usuários.", true);
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

    createForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token || !canManageUsers || !roleOptionsLoaded) return;
        const name = createNameInput.value.trim();
        const email = createEmailInput.value.trim();
        const password = createPasswordInput.value;
        const roleId = createRoleSelect.value;
        if (!name || !email || !password || !roleId) {
            setMessage("Informe nome, e-mail, senha inicial e um perfil explícito.", true);
            return;
        }

        setBusy(true);
        setMessage("Criando usuário…");
        try {
            const response = await fetch("/api/v1/users", {
                method: "POST",
                cache: "no-store",
                headers: {
                    Authorization: `Bearer ${token}`,
                    "Content-Type": "application/json",
                    Accept: "application/json"
                },
                body: JSON.stringify({ name, email, password, role_id: roleId, active: true, must_change_password: true })
            });
            const data = await responseData(response);
            createNameInput.value = "";
            createEmailInput.value = "";
            createPasswordInput.value = "";
            createRoleSelect.value = "";
            if (canViewUsers) {
                setBusy(false);
                await loadUsers();
            }
            setMessage(`Usuário ${data.data?.name || name} criado. A troca de senha é obrigatória no primeiro acesso.`);
        } catch (error) {
            createPasswordInput.value = "";
            setMessage(error.message || "Não foi possível criar o usuário.", true);
        } finally {
            setBusy(false);
        }
    });

    editForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token || !canManageUsers || !editIdInput.value) return;
        const name = editNameInput.value.trim();
        const email = editEmailInput.value.trim();
        if (!name || !email) {
            setMessage("Informe nome e e-mail válidos.", true);
            return;
        }
        setBusy(true);
        setMessage("Salvando dados do usuário…");
        try {
            const response = await fetch(`/api/v1/users/${encodeURIComponent(editIdInput.value)}`, {
                method: "PUT",
                cache: "no-store",
                headers: {
                    Authorization: `Bearer ${token}`,
                    "Content-Type": "application/json",
                    Accept: "application/json"
                },
                body: JSON.stringify({
                    name, email, phone: editPhoneInput.value.trim() || null,
                    department_id: editDepartmentSelect.value || null,
                    industry_id: editIndustrySelect.value || null,
                    ...(editRoleSelect.value && editRoleIds.length <= 1 ? { role_id: editRoleSelect.value } : {})
                })
            });
            await responseData(response);
            closeEdit();
            setBusy(false);
            await loadUsers();
            setMessage("Dados do usuário atualizados.");
        } catch (error) {
            setMessage(error.message || "Não foi possível atualizar os dados do usuário.", true);
        } finally {
            setBusy(false);
        }
    });

    editPasswordForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token || !canManageUsers || !editIdInput.value) return;
        const password = editPasswordInput.value;
        if (!password) {
            setMessage("Informe a senha temporária.", true);
            return;
        }
        if (!globalThis.confirm("Redefinir a senha deste usuário? As sessões existentes serão invalidadas e será obrigatório trocar a senha no próximo acesso.")) {
            editPasswordInput.value = "";
            setMessage("Redefinição de senha cancelada.");
            return;
        }

        const userId = editIdInput.value;
        setBusy(true);
        setMessage("Redefinindo senha…");
        try {
            const response = await fetch(`/api/v1/users/${encodeURIComponent(userId)}`, {
                method: "PUT",
                cache: "no-store",
                headers: {
                    Authorization: `Bearer ${token}`,
                    "Content-Type": "application/json",
                    Accept: "application/json"
                },
                body: JSON.stringify({ password, must_change_password: true })
            });
            await responseData(response);
            setBusy(false);
            setMessage("Senha redefinida. O usuário deverá trocá-la no próximo acesso; as sessões existentes foram invalidadas.");
            await loadUsers();
        } catch (error) {
            setMessage(error.message || "Não foi possível redefinir a senha.", true);
        } finally {
            editPasswordInput.value = "";
            setBusy(false);
        }
    });

    editCancelButton.addEventListener("click", closeEdit);

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
            canViewUsers = false;
            canManageUsers = false;
            roleOptionsLoaded = false;
            requestSequence++;
            metadata = null;
            tableContainer.replaceChildren();
            pageLabel.textContent = "";
            results.classList.add("d-none");
            createPanel.classList.add("d-none");
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            createNameInput.value = "";
            createEmailInput.value = "";
            createPasswordInput.value = "";
            resetRoleOptions();
            closeEdit();
            searchInput.value = "";
            activeSelect.value = "";
            trashedInput.checked = false;
            pageNumber = 1;
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });

    filters.addEventListener("submit", event => {
        event.preventDefault();
        if (busy) return;
        pageNumber = 1;
        loadUsers();
    });

    activeSelect.addEventListener("change", () => {
        if (busy) return;
        pageNumber = 1;
        loadUsers();
    });

    trashedInput.addEventListener("change", () => {
        if (busy) return;
        pageNumber = 1;
        loadUsers();
    });

    previousButton.addEventListener("click", () => {
        if (busy || !metadata || metadata.page <= 1) return;
        pageNumber = metadata.page - 1;
        loadUsers();
    });

    nextButton.addEventListener("click", () => {
        if (busy || !metadata || metadata.page >= metadata.last_page) return;
        pageNumber = metadata.page + 1;
        loadUsers();
    });
})();
