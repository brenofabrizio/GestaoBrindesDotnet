(() => {
    const page = document.getElementById("request-delivery-page");
    if (!page) return;

    const requestId = page.dataset.requestId;
    const loginForm = document.getElementById("request-delivery-login");
    const loginButton = document.getElementById("request-delivery-login-button");
    const logoutButton = document.getElementById("request-delivery-logout");
    const content = document.getElementById("request-delivery-content");
    const deliveryForm = document.getElementById("request-delivery-form");
    const completePanel = document.getElementById("request-delivery-complete");
    const message = document.getElementById("request-delivery-message");
    const errorBox = document.getElementById("request-delivery-error");
    const capabilityBox = document.getElementById("request-delivery-capability");
    const signatureCanvas = document.getElementById("delivery-signature");
    const signatureContext = signatureCanvas.getContext("2d");
    const pdfLink = document.getElementById("request-delivery-pdf");
    const pdfMessage = document.getElementById("request-delivery-pdf-message");

    let token = null;
    let permissions = [];
    let isAdmin = false;
    let busy = false;
    let currentRequest = null;
    let hasSignature = false;
    let pointerActive = false;
    let lastFingerprint = null;
    let idempotencyKey = newIdempotencyKey();
    let pdfObjectUrl = null;

    function newIdempotencyKey() {
        if (window.crypto?.randomUUID) return window.crypto.randomUUID();
        const bytes = new Uint8Array(24);
        window.crypto.getRandomValues(bytes);
        return [...bytes].map(value => value.toString(16).padStart(2, "0")).join("");
    }

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    function setError(text) {
        errorBox.textContent = text;
        errorBox.classList.toggle("d-none", !text);
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        logoutButton.disabled = value;
        for (const control of deliveryForm.querySelectorAll("input, textarea, button")) control.disabled = value;
    }

    function errorDescription(data, status) {
        const fields = data?.errors && Object.values(data.errors).flat().filter(Boolean).join(" ");
        return fields || data?.message || data?.error?.message || data?.detail || data?.title || `Falha na API (${status}).`;
    }

    async function readJson(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const error = new Error(errorDescription(data, response.status));
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
        return readJson(await fetch(path, { cache: "no-store", ...init, headers }));
    }

    function clearSignature() {
        signatureContext.clearRect(0, 0, signatureCanvas.width, signatureCanvas.height);
        hasSignature = false;
    }

    function pointerPoint(event) {
        const rect = signatureCanvas.getBoundingClientRect();
        return {
            x: (event.clientX - rect.left) * signatureCanvas.width / rect.width,
            y: (event.clientY - rect.top) * signatureCanvas.height / rect.height
        };
    }

    function pointerDown(event) {
        if (busy) return;
        event.preventDefault();
        signatureCanvas.setPointerCapture(event.pointerId);
        const point = pointerPoint(event);
        signatureContext.beginPath();
        signatureContext.moveTo(point.x, point.y);
        signatureContext.lineTo(point.x + 0.1, point.y + 0.1);
        signatureContext.strokeStyle = "#172b4d";
        signatureContext.lineWidth = 9;
        signatureContext.lineCap = "round";
        signatureContext.stroke();
        pointerActive = true;
        hasSignature = true;
    }

    function pointerMove(event) {
        if (!pointerActive || busy) return;
        event.preventDefault();
        const point = pointerPoint(event);
        signatureContext.lineTo(point.x, point.y);
        signatureContext.stroke();
        hasSignature = true;
    }

    function pointerUp(event) {
        if (!pointerActive) return;
        pointerActive = false;
        if (signatureCanvas.hasPointerCapture(event.pointerId)) signatureCanvas.releasePointerCapture(event.pointerId);
    }

    function renderRequest(request) {
        currentRequest = request;
        document.getElementById("request-delivery-code").textContent = request.code || "Solicitação";
        document.getElementById("request-delivery-purpose").textContent = request.purpose || "";
        const itemRows = document.getElementById("request-delivery-items");
        itemRows.replaceChildren();
        let deliverableCount = 0;
        for (const item of Array.isArray(request.items) ? request.items : []) {
            const quantity = Math.max(0, Number(item.qty_reserved || 0) - Number(item.qty_delivered || 0));
            if (quantity <= 0) continue;
            deliverableCount += quantity;
            const row = document.createElement("tr");
            const name = document.createElement("th");
            name.scope = "row";
            name.textContent = `${item.code} — ${item.name}`;
            const qty = document.createElement("td");
            qty.textContent = `${quantity} (reservada)`;
            row.append(name, qty);
            itemRows.append(row);
        }
        content.classList.remove("d-none");
        deliveryForm.classList.add("d-none");
        if (request.status !== "pronta") {
            capabilityBox.textContent = `A solicitação está em “${request.status_label || request.status}”; somente solicitações prontas podem ser entregues.`;
            capabilityBox.classList.remove("d-none");
            return;
        }
        if (!isAdmin && !permissions.includes("requests.process")) {
            capabilityBox.textContent = "Sua conta não possui requests.process. A API continua sendo a autoridade para registrar a entrega.";
            capabilityBox.classList.remove("d-none");
            return;
        }
        if (deliverableCount === 0) {
            capabilityBox.textContent = "Não há quantidade reservada pendente para entrega. Não é possível gerar um protocolo sem itens.";
            capabilityBox.classList.remove("d-none");
            return;
        }
        capabilityBox.classList.add("d-none");
        deliveryForm.classList.remove("d-none");
    }

    async function loadRequest() {
        setError("");
        setMessage("Carregando solicitação…");
        setBusy(true);
        try {
            const response = await api(`/api/v1/requests/${encodeURIComponent(requestId)}`);
            const request = response.data || response;
            if (!request || request.flow !== "interna") throw new Error("A API não retornou uma solicitação interna.");
            renderRequest(request);
            setMessage("Solicitação carregada.");
        } catch (error) {
            setError(error.message || "Não foi possível carregar a solicitação.");
            setMessage("Falha ao consultar a API.", true);
        } finally {
            setBusy(false);
        }
    }

    function createPayload() {
        return {
            received_by_name: document.getElementById("delivery-received-name").value.trim(),
            received_by_email: document.getElementById("delivery-received-email").value.trim() || null,
            received_by_document: document.getElementById("delivery-received-document").value.trim() || null,
            received_by_phone: document.getElementById("delivery-received-phone").value.trim() || null,
            notes: document.getElementById("delivery-notes").value.trim() || null,
            signature: signatureCanvas.toDataURL("image/png")
        };
    }

    async function preparePdf(deliveryId) {
        if (!isAdmin && !permissions.includes("deliveries.view")) return;
        pdfMessage.textContent = "Preparando PDF do protocolo…";
        try {
            const response = await fetch(`/api/v1/deliveries/${encodeURIComponent(deliveryId)}/pdf`, {
                cache: "no-store",
                headers: { Accept: "application/pdf", Authorization: `Bearer ${token}` }
            });
            if (!response.ok) throw new Error("O PDF não ficou disponível para esta conta.");
            const blob = await response.blob();
            if (blob.size === 0) throw new Error("A API retornou um PDF vazio.");
            if (pdfObjectUrl) URL.revokeObjectURL(pdfObjectUrl);
            pdfObjectUrl = URL.createObjectURL(blob);
            pdfLink.href = pdfObjectUrl;
            pdfLink.download = `${currentRequest?.code || "protocolo"}.pdf`;
            pdfLink.classList.remove("d-none");
            pdfMessage.textContent = "PDF pronto para baixar.";
        } catch (error) {
            pdfMessage.textContent = error.message || "Não foi possível preparar o PDF.";
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
                    email: document.getElementById("request-delivery-email").value,
                    password: document.getElementById("request-delivery-password").value
                })
            });
            const data = await readJson(response);
            token = data.accessToken || null;
            if (data.mustChangePassword || data.must_change_password) throw new Error("Esta conta precisa trocar a senha antes de registrar entregas.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            permissions = Array.isArray(data.permissions) ? data.permissions : [];
            isAdmin = Boolean(data.isAdmin ?? data.is_admin);
            document.getElementById("request-delivery-password").value = "";
            loginForm.classList.add("d-none");
            document.getElementById("request-delivery-session").classList.remove("d-none");
            document.getElementById("request-delivery-user").textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            await loadRequest();
        } catch (error) {
            if (token) await fetch("/api/v1/auth/logout", { method: "POST", cache: "no-store", headers: { Authorization: `Bearer ${token}`, Accept: "application/json" } }).catch(() => {});
            token = null;
            permissions = [];
            isAdmin = false;
            loginForm.classList.remove("d-none");
            document.getElementById("request-delivery-session").classList.add("d-none");
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
            permissions = [];
            isAdmin = false;
            currentRequest = null;
            if (pdfObjectUrl) URL.revokeObjectURL(pdfObjectUrl);
            pdfObjectUrl = null;
            pdfLink.classList.add("d-none");
            content.classList.add("d-none");
            loginForm.classList.remove("d-none");
            document.getElementById("request-delivery-session").classList.add("d-none");
            clearSignature();
            deliveryForm.reset();
            completePanel.classList.add("d-none");
            deliveryForm.classList.remove("d-none");
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });

    deliveryForm.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy || !deliveryForm.reportValidity()) return;
        if (!hasSignature) {
            setError("A assinatura é obrigatória.");
            return;
        }
        if (!currentRequest || currentRequest.status !== "pronta") {
            setError("O estado da solicitação não permite registrar a entrega.");
            return;
        }
        const payload = createPayload();
        const fingerprint = JSON.stringify(payload);
        if (lastFingerprint !== fingerprint) {
            idempotencyKey = newIdempotencyKey();
            lastFingerprint = fingerprint;
        }
        setError("");
        setBusy(true);
        setMessage("Registrando entrega e gerando protocolo…");
        try {
            const response = await api(`/api/v1/requests/${encodeURIComponent(requestId)}/deliver`, {
                method: "POST",
                headers: { "Idempotency-Key": idempotencyKey },
                body: payload
            });
            const delivery = response.data || {};
            document.getElementById("request-delivery-protocol").textContent = delivery.code || "Protocolo registrado";
            deliveryForm.classList.add("d-none");
            completePanel.classList.remove("d-none");
            setMessage("Entrega confirmada pela API.");
            if (delivery.id) await preparePdf(delivery.id);
        } catch (error) {
            const path = `/api/v1/requests/${encodeURIComponent(requestId)}/deliver`;
            const networkFailure = !error.status || error.status === 0;
            if (networkFailure && window.OfflineQueue?.canQueue("POST", path, payload, idempotencyKey)) {
                try {
                    await window.OfflineQueue.enqueue({ path, body: payload, idempotencyKey });
                    window.OfflineQueue.setAccessToken(token);
                    setError("");
                    setMessage("Sem conexão. A entrega foi guardada neste dispositivo e será sincronizada quando a conexão voltar.");
                    return;
                } catch (queueError) {
                    setError(queueError.message || "Não foi possível guardar a entrega offline.");
                    setMessage("Não foi possível guardar o protocolo.", true);
                    return;
                }
            }
            setError(`${error.message || "A entrega não foi confirmada."} Se a conexão caiu, tente novamente com os mesmos dados para reutilizar a chave; não altere a assinatura nem os campos até conferir o resultado.`);
            setMessage("Não foi possível confirmar o protocolo.", true);
        } finally {
            setBusy(false);
        }
    });

    document.getElementById("delivery-signature-clear").addEventListener("click", () => {
        clearSignature();
        lastFingerprint = null;
    });
    signatureCanvas.addEventListener("pointerdown", pointerDown);
    signatureCanvas.addEventListener("pointermove", pointerMove);
    signatureCanvas.addEventListener("pointerup", pointerUp);
    signatureCanvas.addEventListener("pointercancel", pointerUp);
    window.addEventListener("beforeunload", () => {
        if (pdfObjectUrl) URL.revokeObjectURL(pdfObjectUrl);
    });
})();
