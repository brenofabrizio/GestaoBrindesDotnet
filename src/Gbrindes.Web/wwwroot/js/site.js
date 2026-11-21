(() => {
    const status = document.getElementById("service-status");
    if (!status) return;

    const readinessUrl = status.dataset.readinessUrl;
    if (!readinessUrl) {
        status.textContent = "Não foi possível verificar a disponibilidade.";
        return;
    }

    fetch(readinessUrl, { cache: "no-store", headers: { Accept: "application/json" } })
        .then(async response => {
            const body = await response.json().catch(() => ({}));
            if (!response.ok || body.status !== "ready") {
                throw new Error("Service unavailable");
            }
            status.textContent = "Serviço e banco de dados disponíveis.";
            status.classList.remove("text-secondary", "text-danger");
            status.classList.add("text-success");
        })
        .catch(() => {
            status.textContent = "Serviço ou banco de dados indisponível.";
            status.classList.remove("text-secondary", "text-success");
            status.classList.add("text-danger");
        });
})();
