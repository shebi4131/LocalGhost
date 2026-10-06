(() => {
    const key = "localghost.sidebar.collapsed";
    const apply = collapsed => {
        const shell = document.querySelector(".shell");
        const toggle = document.getElementById("sidebar-toggle");
        if (!shell || !toggle) return;
        shell.classList.toggle("sidebar-collapsed", collapsed);
        toggle.setAttribute("aria-expanded", String(!collapsed));
        toggle.setAttribute("aria-label", collapsed ? "Expand sidebar" : "Collapse sidebar");
        toggle.setAttribute("title", collapsed ? "Expand sidebar" : "Collapse sidebar");
    };
    window.localGhostSidebar = {
        init() { apply(localStorage.getItem(key) === "true"); },
        toggle() {
            const collapsed = !document.querySelector(".shell")?.classList.contains("sidebar-collapsed");
            localStorage.setItem(key, String(collapsed));
            apply(collapsed);
        }
    };
    document.addEventListener("DOMContentLoaded", window.localGhostSidebar.init);
    new MutationObserver(() => { if (document.querySelector(".shell") && !document.querySelector(".shell")?.dataset.sidebarReady) {
        document.querySelector(".shell").dataset.sidebarReady = "true";
        window.localGhostSidebar.init();
    }}).observe(document.body, { childList: true, subtree: true });
})();
