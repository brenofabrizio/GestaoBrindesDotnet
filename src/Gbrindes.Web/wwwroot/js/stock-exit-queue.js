(() => {
    const page = document.getElementById("exit-queue-page");
    if (!page) return;

    const loginForm = document.getElementById("exit-queue-login-form");
    const emailInput = document.getElementById("exit-queue-email");
    const passwordInput = document.getElementById("exit-queue-password");
    const loginButton = document.getElementById("exit-queue-login");
    const sessionPanel = document.getElementById("exit-queue-session");
    const userLabel = document.getElementById("exit-queue-user-label");
    const logoutButton = document.getElementById("exit-queue-logout");
    const queue = document.getElementById("exit-queue-list");
    const codeInput = document.getElementById("exit-queue-code");
    const lookupButton = document.getElementById("exit-queue-lookup");
    const refreshButton = document.getElementById("exit-queue-refresh");
    const results = document.getElementById("exit-queue-results");
    const pickedPanel = document.getElementById("exit-queue-picked");
    const cameraToggle = document.getElementById("exit-queue-camera-toggle");
    const qrFileInput = document.getElementById("exit-queue-qr-file");
    const cameraError = document.getElementById("exit-queue-camera-error");
    const cameraVideo = document.getElementById("exit-queue-camera-video");
    const cameraCanvas = document.getElementById("exit-queue-camera-canvas");
    const message = document.getElementById("exit-queue-message");
    let token = null;
    let busy = false;
    let canConfirm = false;
    let pendingKeys = new Map();
    let cameraStream = null;
    let cameraFrame = null;
    let cameraStarting = false;
    const orderNumber = new Intl.NumberFormat("pt-BR");

    function setMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle("text-danger", isError);
        message.classList.toggle("text-secondary", !isError);
    }

    function setBusy(value) {
        busy = value;
        loginButton.disabled = value;
        logoutButton.disabled = value;
        codeInput.disabled = value;
        lookupButton.disabled = value;
        refreshButton.disabled = value;
        cameraToggle.disabled = value;
        qrFileInput.disabled = value;
        for (const button of page.querySelectorAll("button[data-confirm-order], button[data-attachment-order]")) button.disabled = value;
    }

    async function responseData(response) {
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const fieldMessages = data.errors && Object.values(data.errors).flat().join(" ");
            const error = new Error(fieldMessages || data.message || data.detail || data.title || data.error?.message || `Falha na requisição (${response.status}).`);
            error.status = response.status;
            throw error;
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

    async function downloadAttachment(orderId, button) {
        button.disabled = true;
        try {
            const response = await fetch(`/api/v1/stock/exit-orders/${encodeURIComponent(orderId)}/attachment`, {
                cache: "no-store",
                headers: { Authorization: `Bearer ${token}` }
            });
            if (!response.ok) throw new Error(`Falha ao baixar a nota (${response.status}).`);
            const blobUrl = URL.createObjectURL(await response.blob());
            const link = document.createElement("a");
            link.href = blobUrl;
            const disposition = response.headers.get("Content-Disposition") || "";
            link.download = disposition.match(/filename\*?=(?:UTF-8''|\")?([^\";]+)/i)?.[1] || `NF-${orderId}`;
            link.click();
            setTimeout(() => URL.revokeObjectURL(blobUrl), 1000);
        } catch (error) {
            setMessage(error.message || "Não foi possível baixar a nota.", true);
        } finally {
            button.disabled = false;
        }
    }

    function addText(parent, className, text) {
        const element = document.createElement("p");
        element.className = className;
        element.textContent = text || "—";
        parent.append(element);
        return element;
    }

    function makeConfirmButton(order) {
        if (!canConfirm || order.status !== "autorizada") return null;
        const button = document.createElement("button");
        button.type = "button";
        button.className = "btn btn-primary btn-sm mt-2";
        button.textContent = "Confirmar saída e baixar estoque";
        button.dataset.confirmOrder = order.id;
        button.disabled = busy;
        button.addEventListener("click", () => confirmOrder(order));
        return button;
    }

    function renderOrder(order, detailed = false) {
        const card = document.createElement("article");
        card.className = "card card-body mb-3";
        const heading = document.createElement("h2");
        heading.className = "h5";
        heading.textContent = `${order.code || "Saída"} · ${order.status_label || order.status || "status desconhecido"}`;
        card.append(heading);
        if (typeof order.qr_png === "string" && order.qr_png.startsWith("data:image/png;base64,")) {
            const qrImage = document.createElement("img");
            qrImage.src = order.qr_png;
            qrImage.alt = `QR da saída ${order.code || "autorizada"}`;
            qrImage.width = 140;
            qrImage.height = 140;
            qrImage.className = "bg-white rounded p-2 mb-2";
            qrImage.style.imageRendering = "pixelated";
            card.append(qrImage);
        }
        if (order.item)
            addText(card, "mb-1", `${order.item.code || ""} — ${order.item.name || "Brinde"} · quantidade ${orderNumber.format(order.qty ?? order.quantity ?? 0)}`);
        addText(card, "mb-1", `Finalidade: ${order.purpose || "—"}`);
        if (order.recipient) addText(card, "mb-1", `Destinatário: ${order.recipient}`);
        if (order.department?.name) addText(card, "mb-1", `Departamento: ${order.department.name}`);
        if (order.industry?.name) addText(card, "mb-1", `Indústria: ${order.industry.name}`);
        if (order.authorized_by?.name || order.authorizedBy)
            addText(card, "mb-1 small text-secondary", `Autorizada por ${order.authorized_by?.name || order.authorizedBy}`);
        if (order.notes) addText(card, "mb-1 small text-secondary", `Observações: ${order.notes}`);
        if (order.attachment_url || order.has_attachment || order.hasAttachment) {
            const attachmentButton = document.createElement("button");
            attachmentButton.type = "button";
            attachmentButton.className = "btn btn-outline-secondary btn-sm mt-2";
            attachmentButton.textContent = "Baixar NF anexada";
            attachmentButton.dataset.attachmentOrder = order.id;
            attachmentButton.disabled = busy;
            attachmentButton.addEventListener("click", () => downloadAttachment(order.id, attachmentButton));
            card.append(attachmentButton);
        }
        const button = makeConfirmButton(order);
        if (button) card.append(button);
        if (detailed && order.status !== "autorizada")
            addText(card, "small text-secondary mb-0", "Esta saída já foi tratada e não pode ser confirmada novamente.");
        return card;
    }

    function renderOrders(rows) {
        results.replaceChildren();
        if (!rows.length) {
            addText(results, "card card-body text-secondary", "Nenhuma saída autorizada no momento.");
            return;
        }
        for (const order of rows) results.append(renderOrder(order));
    }

    function renderPicked(order) {
        pickedPanel.replaceChildren();
        if (order) pickedPanel.append(renderOrder(order, true));
    }

    async function loadQueue() {
        setBusy(true);
        setMessage("Carregando saídas autorizadas…");
        try {
            const response = await api("/api/v1/stock/exit-orders?status=autorizada");
            renderOrders(Array.isArray(response.data) ? response.data : []);
            setMessage("Fila atualizada. Confirmações são processadas pela API com controle de estado.");
        } catch (error) {
            results.replaceChildren();
            setMessage(error.message || "Não foi possível carregar a fila.", true);
        } finally {
            setBusy(false);
        }
    }

    async function lookupOrder() {
        if (busy || !token) return;
        const code = codeInput.value.trim();
        if (!code) {
            setMessage("Informe o código da saída autorizada.", true);
            return;
        }
        setBusy(true);
        setMessage("Localizando saída…");
        try {
            const response = await api(`/api/v1/stock/exit-orders/lookup?code=${encodeURIComponent(code)}`);
            renderPicked(response.data || null);
            if (response.data?.status === "autorizada") setMessage("Saída autorizada localizada.");
            else setMessage(response.data?.status_label || "Esta saída já foi tratada.", true);
        } catch (error) {
            renderPicked(null);
            setMessage(error.message || "Não foi possível localizar a saída.", true);
        } finally {
            setBusy(false);
        }
    }

    function extractOrderCode(raw) {
        const text = String(raw || "").trim();
        const match = text.match(/SAI-\d{4}-\d+/i);
        if (match) return match[0].toUpperCase();
        try {
            const nested = new URL(text, window.location.origin).searchParams.get("code");
            const nestedMatch = nested?.match(/SAI-\d{4}-\d+/i);
            if (nestedMatch) return nestedMatch[0].toUpperCase();
            if (nested) return nested.trim().toUpperCase();
        } catch {
            // Non-URL QR payloads are treated as literal order codes.
        }
        return text.toUpperCase();
    }

    function decodeQrImage(source, sourceWidth, sourceHeight) {
        if (typeof globalThis.jsQR !== "function") throw new Error("O leitor de QR não foi carregado.");
        if (!sourceWidth || !sourceHeight) return null;
        const context = cameraCanvas.getContext("2d", { willReadFrequently: true });
        if (!context) throw new Error("Não foi possível processar a imagem neste navegador.");
        const baseScale = Math.min(1, 1800 / Math.max(sourceWidth, sourceHeight));
        const scales = [...new Set([baseScale, Math.min(1.5, baseScale * 1.5), Math.min(2, baseScale * 2)])];
        for (const angle of [0, 90, 180, 270]) {
            for (const scale of scales) {
                const width = Math.max(1, Math.round(sourceWidth * scale));
                const height = Math.max(1, Math.round(sourceHeight * scale));
                cameraCanvas.width = angle % 180 ? height : width;
                cameraCanvas.height = angle % 180 ? width : height;
                context.save();
                context.translate(cameraCanvas.width / 2, cameraCanvas.height / 2);
                context.rotate(angle * Math.PI / 180);
                context.drawImage(source, -width / 2, -height / 2, width, height);
                context.restore();
                const pixels = context.getImageData(0, 0, cameraCanvas.width, cameraCanvas.height);
                const result = globalThis.jsQR(pixels.data, pixels.width, pixels.height, { inversionAttempts: "attemptBoth" });
                if (result?.data) return result.data;
            }
        }
        return null;
    }

    function stopCamera() {
        if (cameraFrame !== null) cancelAnimationFrame(cameraFrame);
        cameraFrame = null;
        if (cameraStream) cameraStream.getTracks().forEach(track => track.stop());
        cameraStream = null;
        cameraVideo.srcObject = null;
        cameraVideo.classList.add("d-none");
        cameraToggle.textContent = "Abrir câmera para escanear QR";
    }

    function scanCameraFrame() {
        if (!cameraStream) return;
        if (cameraVideo.readyState >= HTMLMediaElement.HAVE_ENOUGH_DATA && cameraVideo.videoWidth && cameraVideo.videoHeight) {
            cameraCanvas.width = cameraVideo.videoWidth;
            cameraCanvas.height = cameraVideo.videoHeight;
            const context = cameraCanvas.getContext("2d", { willReadFrequently: true });
            try {
                context.drawImage(cameraVideo, 0, 0, cameraCanvas.width, cameraCanvas.height);
                const pixels = context.getImageData(0, 0, cameraCanvas.width, cameraCanvas.height);
                const result = globalThis.jsQR(pixels.data, pixels.width, pixels.height, { inversionAttempts: "attemptBoth" });
                if (result?.data) {
                    codeInput.value = extractOrderCode(result.data);
                    cameraError.textContent = "QR reconhecido; consultando a saída…";
                    stopCamera();
                    lookupOrder();
                    return;
                }
            } catch {
                cameraError.textContent = "Não foi possível ler o quadro da câmera.";
                stopCamera();
                return;
            }
        }
        cameraFrame = requestAnimationFrame(scanCameraFrame);
    }

    async function toggleCamera() {
        if (cameraStream) {
            stopCamera();
            cameraError.textContent = "Leitura pela câmera encerrada.";
            return;
        }
        if (cameraStarting || busy) return;
        cameraError.textContent = "";
        if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
            cameraError.textContent = "A câmera requer HTTPS (ou localhost) e suporte do navegador.";
            return;
        }
        if (typeof globalThis.jsQR !== "function") {
            cameraError.textContent = "O leitor de QR não foi carregado.";
            return;
        }
        cameraStarting = true;
        cameraToggle.disabled = true;
        try {
            cameraStream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" }, audio: false });
            if (!token || document.hidden) {
                stopCamera();
                return;
            }
            cameraVideo.srcObject = cameraStream;
            await cameraVideo.play();
            cameraVideo.classList.remove("d-none");
            cameraToggle.textContent = "Fechar câmera";
            cameraError.textContent = "Aponte a câmera para o QR da saída autorizada.";
            cameraFrame = requestAnimationFrame(scanCameraFrame);
        } catch {
            stopCamera();
            cameraError.textContent = "Não foi possível abrir a câmera. Confira a permissão do dispositivo.";
        } finally {
            cameraStarting = false;
            cameraToggle.disabled = busy;
        }
    }

    async function scanQrFile(event) {
        if (busy || !token) return;
        const file = event.target.files?.[0];
        if (!file) return;
        stopCamera();
        setBusy(true);
        cameraError.textContent = "Lendo QR da imagem…";
        let source = null;
        let objectUrl = null;
        try {
            if (file.size > 15 * 1024 * 1024) throw new Error("A imagem deve ter no máximo 15 MB.");
            if (typeof globalThis.jsQR !== "function") throw new Error("O leitor de QR não foi carregado.");
            if (typeof createImageBitmap === "function") {
                source = await createImageBitmap(file);
            } else {
                objectUrl = URL.createObjectURL(file);
                source = await new Promise((resolve, reject) => {
                    const image = new Image();
                    image.onload = () => resolve(image);
                    image.onerror = () => reject(new Error("Não foi possível abrir a imagem."));
                    image.src = objectUrl;
                });
            }
            const rawCode = decodeQrImage(source, source.width || source.naturalWidth, source.height || source.naturalHeight);
            if (!rawCode) throw new Error("Nenhum QR legível foi encontrado na imagem.");
            codeInput.value = extractOrderCode(rawCode);
            cameraError.textContent = "QR reconhecido; consultando a saída…";
            setBusy(false);
            await lookupOrder();
        } catch (error) {
            cameraError.textContent = error.message || "Não foi possível ler o QR desta imagem.";
        } finally {
            if (typeof source?.close === "function") source.close();
            if (objectUrl) URL.revokeObjectURL(objectUrl);
            qrFileInput.value = "";
            setBusy(false);
        }
    }

    async function confirmOrder(order) {
        if (busy || !token || !canConfirm || order.status !== "autorizada") return;
        const confirmed = window.confirm(`Confirmar a saída ${order.code} de ${order.qty ?? order.quantity} unidade(s) de ${order.item?.name || "brinde"}? Esta ação baixa o estoque e não pode ser desfeita por esta tela.`);
        if (!confirmed) return;
        let key = pendingKeys.get(order.id);
        if (!key) {
            key = typeof globalThis.crypto?.randomUUID === "function"
                ? globalThis.crypto.randomUUID()
                : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
            pendingKeys.set(order.id, key);
        }
        setBusy(true);
        setMessage(`Confirmando saída ${order.code}…`);
        try {
            const response = await api(`/api/v1/stock/exit-orders/${encodeURIComponent(order.id)}/confirm`, {
                method: "POST",
                headers: { "Idempotency-Key": key }
            });
            pendingKeys.delete(order.id);
            const stock = response.data?.stock;
            setMessage(`Saída confirmada. Saldo físico: ${stock?.onHand ?? "não informado"}; reservado: ${stock?.reserved ?? "não informado"}.`);
            if (pickedPanel.querySelector(`[data-confirm-order="${CSS.escape(order.id)}"]`)) renderPicked(null);
            await refreshQueueSilently();
        } catch (error) {
            const path = `/api/v1/stock/exit-orders/${encodeURIComponent(order.id)}/confirm`;
            const networkFailure = !error.status || error.status === 0;
            if (networkFailure && window.OfflineQueue?.canQueue("POST", path, {}, key)) {
                try {
                    await window.OfflineQueue.enqueue({ path, body: {}, idempotencyKey: key });
                    window.OfflineQueue.setAccessToken(token);
                    pendingKeys.delete(order.id);
                    setMessage(`Sem conexão. A confirmação da saída ${order.code} foi guardada neste dispositivo e será sincronizada quando a conexão voltar.`);
                    return;
                } catch (queueError) {
                    setMessage(queueError.message || "Não foi possível guardar a confirmação offline.", true);
                    return;
                }
            }
            setMessage(`${error.message || "Falha ao confirmar."} Se o resultado for incerto, tente novamente sem alterar esta saída; a chave idempotente será reutilizada.`, true);
        } finally {
            setBusy(false);
        }
    }

    async function refreshQueueSilently() {
        try {
            const response = await api("/api/v1/stock/exit-orders?status=autorizada");
            renderOrders(Array.isArray(response.data) ? response.data : []);
        } catch {
            // Preserve the confirmation result; an explicit refresh can report list errors.
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
            if (data.mustChangePassword) throw new Error("Esta conta precisa trocar a senha antes de confirmar saídas.");
            if (!token) throw new Error("A autenticação não retornou uma sessão válida.");
            const permissions = Array.isArray(data.permissions) ? data.permissions : [];
            if (!permissions.includes("stock.exit_confirm"))
                throw new Error("A conta não possui a permissão stock.exit_confirm para confirmar saídas.");
            canConfirm = true;
            loginForm.classList.add("d-none");
            sessionPanel.classList.remove("d-none");
            userLabel.textContent = `Conectado como ${data.user?.name || data.user?.email || "usuário"}.`;
            queue.classList.remove("d-none");
            await loadQueue();
        } catch (error) {
            if (token) {
                await fetch("/api/v1/auth/logout", {
                    method: "POST",
                    cache: "no-store",
                    headers: { Authorization: `Bearer ${token}`, Accept: "application/json" }
                }).catch(() => {});
            }
            token = null;
            canConfirm = false;
            queue.classList.add("d-none");
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            setMessage(error.message || "Não foi possível entrar.", true);
        } finally {
            setBusy(false);
        }
    });

    lookupButton.addEventListener("click", lookupOrder);
    cameraToggle.addEventListener("click", toggleCamera);
    qrFileInput.addEventListener("change", scanQrFile);
    codeInput.addEventListener("keydown", event => {
        if (event.key === "Enter") {
            event.preventDefault();
            lookupOrder();
        }
    });
    refreshButton.addEventListener("click", loadQueue);
    window.addEventListener("pagehide", stopCamera);
    document.addEventListener("visibilitychange", () => {
        if (document.hidden && cameraStream) stopCamera();
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
            stopCamera();
            token = null;
            canConfirm = false;
            pendingKeys.clear();
            queue.classList.add("d-none");
            results.replaceChildren();
            pickedPanel.replaceChildren();
            codeInput.value = "";
            sessionPanel.classList.add("d-none");
            loginForm.classList.remove("d-none");
            setMessage("Sessão encerrada.");
            setBusy(false);
        }
    });
})();
