(() => {
    const page = document.getElementById("roles-page");
    if (!page) return;

    const loginForm = document.getElementById("roles-login-form");
    const emailInput = document.getElementById("roles-email");
    const passwordInput = document.getElementById("roles-password");
    const loginButton = document.getElementById("roles-login");
    const sessionPanel = document.getElementById("roles-session");
    const userLabel = document.getElementById("roles-user-label");
    const logoutButton = document.getElementById("roles-logout");
    const roleList = document.getElementById("role-list");
    const message = document.getElementById("roles-message");
    let token = null;
    let busy = false;
    const numberFormat = new Intl.NumberFormat("pt-BR");

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        logoutButton.disabled = value;
        for (const input of roleList.querySelectorAll("input[type=checkbox]"))
            input.disabled = value || input.dataset.locked === "true";
        for (const button of roleList.querySelectorAll("button[data-role-save]"))
            button.disabled = value || button.dataset.dirty !== "true";
    }

    async function responseData(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const fieldMessages = data.errors && Object.values(data.errors).flat().join(" ");
            throw new Error(fieldMessages || data.message || data.title || data.error || data.detail || `Falha na requisição (${response.status}).`);
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

    function samePermissions(left, right) {
        return left.length === right.length && left.every((permission, index) => permission === right[index]);
    }

    function saveState(role, button) {
        const dirty = !samePermissions(role.permissions, role.originalPermissions);
        button.dataset.dirty = dirty ? "true" : "false";
        button.disabled = busy || !dirty;
    }

    function renderRoles(roles, groups) {
        roleList.replaceChildren();
        if (!roles.length) {
            const empty = document.createElement("p");
            empty.className = "card card-body text-secondary";
            empty.textContent = "Nenhum perfil encontrado. Nenhum perfil ou grant foi criado automaticamente.";
            roleList.append(empty);
            return;
        }

        const knownPermissions = new Set(groups.flatMap(group => group.permissions.map(permission => permission.slug)));
        for (const item of roles) {
            const role = {
                ...item,
                permissions: [...new Set(Array.isArray(item.permissions) ? item.permissions : [])].sort(),
                originalPermissions: [...new Set(Array.isArray(item.permissions) ? item.permissions : [])].sort()
            };
            role.unknownPermissions = role.permissions.filter(permission => !knownPermissions.has(permission));
            const card = document.createElement("article");
            card.className = "card card-body mb-3";
            const title = document.createElement("h2");
            title.className = "h5";
            title.textContent = `${role.name} · ${numberFormat.format(role.users_count || 0)} usuários`;
            card.append(title);
            if (role.slug || role.description) {
                const metadata = document.createElement("p");
                metadata.className = "small text-secondary";
                metadata.textContent = [role.slug, role.description].filter(Boolean).join(" · ");
                card.append(metadata);
            }

            if (!role.editable) {
                const adminNote = document.createElement("p");
                adminNote.className = "mb-0 small";
                adminNote.textContent = "Este perfil possui bypass administrativo reservado e não pode ser alterado nesta tela.";
                card.append(adminNote);
            } else if (role.unknownPermissions.length) {
                const warning = document.createElement("p");
                warning.className = "alert alert-warning mb-3";
                warning.textContent = `Edição bloqueada para preservar grants fora do catálogo atual: ${role.unknownPermissions.join(", ")}. Homologue o mapeamento antes de removê-los.`;
                card.append(warning);
            }

            const fieldset = document.createElement("fieldset");
            fieldset.disabled = !role.editable || role.unknownPermissions.length > 0;
            for (const group of groups) {
                const section = document.createElement("fieldset");
                section.className = "border rounded p-3 mb-3";
                const legend = document.createElement("legend");
                legend.className = "float-none w-auto px-2 fs-6 fw-semibold";
                legend.textContent = group.module;
                section.append(legend);
                for (const permission of group.permissions) {
                    const wrapper = document.createElement("div");
                    wrapper.className = "form-check form-check-inline mb-2";
                    const input = document.createElement("input");
                    input.className = "form-check-input";
                    input.type = "checkbox";
                    input.id = `role-${role.id}-${permission.slug}`;
                    input.checked = role.permissions.includes(permission.slug);
                    input.dataset.locked = String(!role.editable || role.unknownPermissions.length > 0);
                    const label = document.createElement("label");
                    label.className = "form-check-label";
                    label.htmlFor = input.id;
                    label.textContent = `${permission.name} (${permission.slug})`;
                    input.addEventListener("change", () => {
                        if (busy) return;
                        if (input.checked && !role.permissions.includes(permission.slug))
                            role.permissions.push(permission.slug);
                        else if (!input.checked)
                            role.permissions = role.permissions.filter(value => value !== permission.slug);
                        role.permissions.sort();
                        saveState(role, saveButton);
                    });
                    wrapper.append(input, label);
                    section.append(wrapper);
                }
                fieldset.append(section);
            }
            card.append(fieldset);
            const actions = document.createElement("div");
            actions.className = "d-flex flex-wrap align-items-center gap-3";
            const saveButton = document.createElement("button");
            saveButton.type = "button";
            saveButton.className = "btn btn-sm btn-primary";
            saveButton.textContent = "Salvar grants explícitos";
            saveButton.dataset.roleSave = role.id;
            saveButton.dataset.dirty = "false";
            saveButton.disabled = !role.editable || role.unknownPermissions.length > 0;
            saveButton.addEventListener("click", async () => {
                if (busy || !role.editable || role.unknownPermissions.length || !saveButton.dataset.dirty) return;
                const confirmed = window.confirm(`Salvar exatamente estes ${role.permissions.length} grants no perfil “${role.name}”? A alteração afetará ${role.users_count || 0} usuários. Nenhuma matriz de origem será importada.`);
                if (!confirmed) return;
                setBusy(true);
                setMessage(`Salvando permissões de ${role.name}…`);
                try {
                    await api(`/api/v1/roles/${encodeURIComponent(role.id)}/permissions`, {
                        method: "PUT",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({ permissions: role.permissions })
                    });
                    setMessage(`Grants salvos para ${role.name}.`);
                    setBusy(false);
                    await loadRoles();
                } catch (error) {
                    setMessage(error.message || "Não foi possível salvar os grants.", true);
                } finally {
                    setBusy(false);
                }
            });
            const count = document.createElement("span");
            count.className = "small text-secondary";
            count.textContent = role.unknownPermissions.length ? "Edição desabilitada até mapear os grants desconhecidos." : "Nenhuma alteração foi enviada até confirmar Salvar.";
            actions.append(saveButton, count);
            card.append(actions);
            roleList.append(card);
        }
        setBusy(false);
    }

    async function loadRoles() {
        setBusy(true);
        setMessage("Carregando perfis e catálogo .NET…");
        try {
            const [roleResponse, permissionResponse] = await Promise.all([
                api("/api/v1/roles"),
                api("/api/v1/permissions")
            ]);
            renderRoles(Array.isArray(roleResponse.data) ? roleResponse.data : [],
                Array.isArray(permissionResponse.data) ? permissionResponse.data : []);
            roleList.classList.remove("d-none");
            setMessage("Perfis carregados. Grants só serão alterados após confirmação explícita.");
        } catch (error) {
            roleList.replaceChildren();
            setMessage(error.message || "Não foi possível carregar os perfis.", true);
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
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de gerenciar perfis.");
            if (!Array.isArray(data.permissions) || !data.permissions.includes("roles.manage"))
                throw new Error("A conta não possui permissão para gerenciar perfis.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            setBusy(false);
            await loadRoles();
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
            roleList.replaceChildren();
            roleList.classList.add("d-none");
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });
})();
