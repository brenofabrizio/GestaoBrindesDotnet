(() => {
    "use strict";

    const DB_NAME = "gbrindes-offline";
    const DB_VERSION = 1;
    const STORE = "outbox";
    const replayable = [
        /^\/api\/v1\/requests$/,
        /^\/api\/v1\/requests\/[^/]+\/deliver$/,
        /^\/api\/v1\/trade-requests$/,
        /^\/api\/v1\/trade-requests\/[^/]+\/(withdraw|receive)$/,
        /^\/api\/v1\/stock\/(entries|exits|adjustments|transfers|reversals)$/,
        /^\/api\/v1\/items\/[^/]+\/entry$/,
        /^\/api\/v1\/stock\/exit-orders\/[^/]+\/confirm$/,
        /^\/api\/v1\/events\/[^/]+\/(returns|withdrawals)$/
    ];
    let databasePromise;
    let syncing = false;
    let accessToken = localStorage.getItem("gbrindes.auth.token") || null;

    function openDatabase() {
        if (databasePromise) return databasePromise;
        if (!("indexedDB" in window)) return Promise.reject(new Error("Armazenamento offline indisponível neste navegador."));
        databasePromise = new Promise((resolve, reject) => {
            const request = indexedDB.open(DB_NAME, DB_VERSION);
            request.onupgradeneeded = () => {
                const db = request.result;
                if (db.objectStoreNames.contains(STORE)) return;
                const store = db.createObjectStore(STORE, { keyPath: "id" });
                store.createIndex("createdAt", "createdAt", { unique: false });
                store.createIndex("status", "status", { unique: false });
            };
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error || new Error("Não foi possível abrir o armazenamento offline."));
        });
        return databasePromise;
    }

    function id() {
        return crypto?.randomUUID?.() || `offline-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    }

    function isReplayable(method, path, body, idempotencyKey) {
        return method === "POST"
            && typeof path === "string"
            && replayable.some(pattern => pattern.test(path))
            && body !== undefined
            && body !== null
            && typeof body !== "string"
            && !(body instanceof FormData)
            && (!body.__multipart || isValidMultipartBody(body))
            && typeof idempotencyKey === "string"
            && /^[A-Za-z0-9_-]{8,80}$/.test(idempotencyKey);
    }

    function isValidMultipartBody(body) {
        return body.__multipart === true
            && body.fields && typeof body.fields === "object"
            && body.file && typeof body.file === "object"
            && typeof body.file.field === "string"
            && typeof body.file.name === "string"
            && typeof body.file.type === "string"
            && typeof body.file.dataBase64 === "string"
            && body.file.dataBase64.length > 0
            && body.file.dataBase64.length <= 14 * 1024 * 1024;
    }

    function buildRequestBody(entry) {
        if (!entry.body?.__multipart) {
            return {
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(entry.body)
            };
        }
        const multipart = new FormData();
        for (const [name, value] of Object.entries(entry.body.fields || {})) {
            if (value !== null && value !== undefined) multipart.append(name, String(value));
        }
        const file = entry.body.file;
        const bytes = Uint8Array.from(atob(file.dataBase64), character => character.charCodeAt(0));
        multipart.append(file.field, new Blob([bytes], { type: file.type }), file.name);
        return { headers: {}, body: multipart };
    }

    function transaction(mode, callback) {
        return openDatabase().then(db => new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, mode);
            const result = callback(tx.objectStore(STORE));
            tx.oncomplete = () => resolve(result);
            tx.onerror = () => reject(tx.error || new Error("Operação offline falhou."));
        }));
    }

    async function list() {
        const db = await openDatabase();
        return new Promise((resolve, reject) => {
            const request = db.transaction(STORE, "readonly").objectStore(STORE).getAll();
            request.onsuccess = () => resolve((request.result || []).sort((a, b) => a.createdAt - b.createdAt));
            request.onerror = () => reject(request.error);
        });
    }

    async function update(entry) {
        return transaction("readwrite", store => store.put(entry));
    }

    async function remove(entryId) {
        return transaction("readwrite", store => store.delete(entryId));
    }

    async function enqueue({ method = "POST", path, body, idempotencyKey }) {
        if (!isReplayable(method, path, body, idempotencyKey)) {
            throw new Error("Esta operação não pode ser armazenada para sincronização offline.");
        }
        const entry = {
            id: id(),
            method,
            path,
            body,
            idempotencyKey,
            status: "pending",
            attempts: 0,
            createdAt: Date.now(),
            lastError: ""
        };
        await transaction("readwrite", store => store.add(entry));
        window.dispatchEvent(new CustomEvent("gbrindes:offline-queued", { detail: entry }));
        return entry;
    }

    async function replay(entry, token) {
        const requestBody = buildRequestBody(entry);
        const response = await fetch(entry.path, {
            method: entry.method,
            cache: "no-store",
            headers: {
                Accept: "application/json",
                "Idempotency-Key": entry.idempotencyKey,
                ...requestBody.headers,
                ...(token ? { Authorization: `Bearer ${token}` } : {})
            },
            body: requestBody.body
        });
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            const error = new Error(data.error?.message || data.message || data.detail || data.title || `Falha na sincronização (${response.status}).`);
            error.status = response.status;
            error.code = data.error?.code || data.code || data.title;
            throw error;
        }
        return data;
    }

    async function sync(token = accessToken) {
        if (syncing || !navigator.onLine) return { synced: 0, pending: await count() };
        const entries = (await list()).filter(entry => entry.status !== "failed");
        if (!entries.length) return { synced: 0, pending: 0 };
        if (!token) return { synced: 0, pending: entries.length };
        syncing = true;
        let synced = 0;
        try {
            for (const entry of entries) {
                entry.status = "syncing";
                entry.attempts = (entry.attempts || 0) + 1;
                await update(entry);
                try {
                    await replay(entry, token);
                    await remove(entry.id);
                    synced++;
                } catch (error) {
                    const transient = !error.status || error.status === 408 || error.status === 429 || error.status >= 500;
                    entry.status = transient ? "pending" : "failed";
                    entry.lastError = error.message;
                    await update(entry);
                    if (transient) break;
                }
            }
        } finally {
            syncing = false;
        }
        return { synced, pending: await count() };
    }

    async function count() {
        try { return (await list()).length; } catch { return 0; }
    }

    window.OfflineQueue = {
        canQueue: isReplayable,
        enqueue,
        sync,
        pendingCount: count,
        setAccessToken: token => { accessToken = token || null; },
        clear: async () => transaction("readwrite", store => store.clear())
    };

    window.addEventListener("storage", event => {
        if (event.key === "gbrindes.auth.token") accessToken = event.newValue || null;
    });
    window.addEventListener("online", () => sync());
    window.addEventListener("focus", () => sync());
})();
