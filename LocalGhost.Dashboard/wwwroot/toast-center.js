(() => {
    const stack = document.createElement("div");
    stack.className = "localghost-toast-stack";
    stack.setAttribute("aria-live", "polite");
    document.body.append(stack);

    window.localGhostToast = (title, detail = "", type = "success") => {
        const toast = document.createElement("div");
        toast.className = `localghost-toast ${type === "error" ? "error" : "success"}`;
        toast.setAttribute("role", type === "error" ? "alert" : "status");
        const icon = document.createElement("span");
        icon.className = "toast-icon";
        icon.textContent = type === "error" ? "!" : "✓";
        const copy = document.createElement("div");
        const heading = document.createElement("strong");
        heading.textContent = title;
        copy.append(heading);
        if (detail) {
            const body = document.createElement("small");
            body.textContent = detail;
            copy.append(body);
        }
        const close = document.createElement("button");
        close.type = "button";
        close.setAttribute("aria-label", "Dismiss notification");
        close.textContent = "×";
        close.addEventListener("click", () => toast.remove());
        toast.append(icon, copy, close);
        stack.append(toast);
        setTimeout(() => toast.remove(), 10000);
    };

    const consumeAlerts = () => {
        document.querySelectorAll("[data-global-toast]:not([data-toast-shown])").forEach(alert => {
            alert.dataset.toastShown = "true";
            const title = alert.querySelector("strong")?.textContent?.trim() || alert.textContent.trim();
            const detail = alert.querySelector("small")?.textContent?.trim() || "";
            window.localGhostToast(title, detail, alert.classList.contains("error") ? "error" : "success");
            alert.classList.add("toast-consumed");
        });
    };

    consumeAlerts();
    document.addEventListener("DOMContentLoaded", consumeAlerts);
    new MutationObserver(consumeAlerts).observe(document.body, { childList: true, subtree: true });
})();
