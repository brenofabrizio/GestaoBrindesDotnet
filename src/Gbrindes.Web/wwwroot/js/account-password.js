(() => {
    const page = document.getElementById("account-password-page");
    if (!page) return;

    const loginForm = document.getElementById("account-password-login-form");
    const emailInput = document.getElementById("account-password-email");
    const loginPasswordInput = document.getElementById("account-password-login-password");
    const loginButton = document.getElementById("account-password-login-button");
    const sessionPanel = document.getElementById("account-password-session");
    const userLabel = document.getElementById("account-password-user-label");
    const logoutButton = document.getElementById("account-password-logout");
    const changePanel = document.getElementById("account-password-change-panel");
    const changeForm = document.getElementById("account-password-change-form");
    const currentPasswordInput = document.getElementById("account-password-current-password");
    const newPasswordInput = document.getElementById("account-password-new-password");
    const confirmationInput = document.getElementById("account-password-confirm-password");
    const changeButton = document.getElementById("account-password-change-button");
    const message = document.getElementById("account-password-message");

    let token = null;
    let busy = false;

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        logoutButton.disabled = value || !token;
        changeButton.disabled = value;
        emailInput.disabled = value;
        loginPasswordInput.disabled = value;
        currentPasswordInput.disabled = value;
        newPasswordInput.disabled = value;
        confirmationInput.disabled = value;
    }

    async function responseData(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const fieldMessages = data.errors && Object.values(data.errors).flat().join(" ");
            throw new Error(fieldMessages || data.detail || data.message || data.title || data.error || `Falha na requisição (${response.status}).`);
        }
        return data;
    }

    function clearPasswordFields() {
        loginPasswordInput.value = "";
        currentPasswordInput.value = "";
        newPasswordInput.value = "";
        confirmationInput.value = "";
    }

    function resetSession(messageText) {
        token = null;
        clearPasswordFields();
        loginForm.classList.remove("d-none");
        sessionPanel.classList.add("d-none");
        changePanel.classList.add("d-none");
        userLabel.textContent = "";
        setMessage(messageText);
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
                body: JSON.stringify({ email: emailInput.value, password: loginPasswordInput.value })
            });
            const data = await responseData(response);
            token = data.accessToken || null;
            clearPasswordFields();
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");

            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            changePanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            setMessage(data.mustChangePassword
                ? "Sua conta exige troca de senha para continuar."
                : "Sessão autenticada. Informe a senha atual e a nova senha.");
        } catch (error) {
            token = null;
            clearPasswordFields();
            setMessage(error.message || "Não foi possível entrar.", true);
        } finally {
            setBusy(false);
        }
    });

    changeForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !token) return;
        const currentPassword = currentPasswordInput.value;
        const newPassword = newPasswordInput.value;
        const newPasswordConfirmation = confirmationInput.value;
        if (!currentPassword || !newPassword || !newPasswordConfirmation) {
            setMessage("Preencha a senha atual, a nova senha e a confirmação.", true);
            return;
        }
        if (newPassword.length < 8 || !/\d/.test(newPassword)) {
            setMessage("A nova senha deve ter pelo menos 8 caracteres e incluir um número.", true);
            return;
        }
        if (newPassword !== newPasswordConfirmation) {
            setMessage("A confirmação não confere com a nova senha.", true);
            return;
        }

        setBusy(true);
        setMessage("Alterando senha…");
        try {
            const response = await fetch("/api/v1/auth/change-password", {
                method: "POST",
                cache: "no-store",
                headers: {
                    Authorization: `Bearer ${token}`,
                    "Content-Type": "application/json",
                    Accept: "application/json"
                },
                body: JSON.stringify({ currentPassword, newPassword, newPasswordConfirmation })
            });
            const data = await responseData(response);
            clearPasswordFields();
            setMessage(data.message || "Senha alterada com sucesso.");
        } catch (error) {
            clearPasswordFields();
            setMessage(error.message || "Não foi possível alterar a senha.", true);
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
            resetSession("Sessão encerrada.");
            setBusy(false);
        }
    });
})();
