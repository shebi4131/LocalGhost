(() => {
    document.addEventListener("pointerdown", event => {
        document.querySelectorAll(".project-actions-menu[open]").forEach(menu => {
            if (!menu.contains(event.target)) menu.open = false;
        });
    });
    document.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            document.querySelectorAll(".project-actions-menu[open]").forEach(menu => menu.open = false);
        }
    });
})();
