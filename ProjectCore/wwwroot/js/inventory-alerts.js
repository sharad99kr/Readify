(() => {

    "use strict";

    const token = document.querySelector(
        'input[name="__RequestVerificationToken"]')?.value ?? "";

    const stateEl = document.getElementById("connection-state");
    const briefingEl = document.getElementById("briefing");
    const feedEl = document.getElementById("alert-feed");
    const runBtn = document.getElementById("run-check");
    const seedBtn = document.getElementById("seed-stock");

    //--- SignalR connection with automatic reconnect ---
    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/inventory-alerts")
        .withAutomaticReconnect()
        .build();

    connection.on("ReceiveAlert", renderAlert);
    connection.on("ReceiveDiscrepancyAlert", renderAlert);

    connection.onreconnecting(() => setState("Reconnecting...", "text-warning"));
    connection.onreconnecting(() => setState("Connected", "text-success"));
    connection.onclose(() => setState("Disconnected", "text-danger"));

    connection.start()
        .then(() => setState("Connected", "text-success"))
        .catch(() => setState("connection failed", "text-danger"));

    //--- Buttons ---
    runBtn.addEventListener("click", () => post("/Admin/Inventory/RunCheck", res => {
        if (res.briefing) {
            briefingEl.textContent = res.briefing;
            briefingEl.classList.remove("d-none");
        }
    }));

    seedBtn.addEventListener("click", () => post("/Admin/Inventory/SeedStock", res => {
        setState(`Seeded stock on ${res.updated} products. Now run a check`, "text-muted");
    }));

    //--- Helpers ---
    async function post(url, onOk) {
        try {

            const res = await fetch(url, {
                method: "POST",
                headers: { "RequestVerificationToken": token }
            });

            if (!res.ok) {
                throw new Error(`HTTP ${res.status}`);
            }
            onOk(await res.json());

        } catch (err) {
            setState("Request failed - see console", "text-danger");
            console.error("[Inventory]", err);
        }
    }

    function renderAlert(alert) {
        const urgent = alert.priority === "Urgent";
        const card = document.createElement("div");
        // Urgent = red, Routine = amber/yellow. Bootstrap utility classes;

        card.className = "p-2 rounded border " +
            (urgent ? "border-danger bg-danger-subtle" : "border-warning bg-warning-subtle");

        card.innerHTML =
            `<strong>${escapeHtml(alert.productName)}</strong> ` +
            `<span class="badge ${urgent ? "bg-danger" : "bg-warning text-dark"}">${alert.priority}</span>` +
            `<div class="small">${escapeHtml(alert.message)}</div>` +
            `<div class="text-muted" style="font-size:.75rem">${new Date(alert.timestampUtc).toLocaleTimeString()}</div>`;
            feedEl.prepend(card); //newest on top
    }

    function setState(text, cls) {
        if (!stateEl) {
            return;
        }
        stateEl.textContent = text;
        stateEl.className = "small mb-2 " + cls;
    }

    function escapeHtml(s) {
        const d = document.createElement("div");
        d.textContent = s ?? "";
        return d.innerHTML;
    }


})();