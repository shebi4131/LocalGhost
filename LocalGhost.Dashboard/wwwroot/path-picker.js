window.chooseLocalPath = async button => {
    const input = document.querySelector(`[name="${button.dataset.target}"]`);
    const token = button.closest("form")?.querySelector('input[name="__RequestVerificationToken"]');
    if (!input || !token) return;

    const original = button.innerHTML;
    button.disabled = true;
    button.innerHTML = '<i class="path-spinner"></i><span>Opening</span>';

    try {
        const response = await fetch("/api/path-picker", {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Content-Type": "application/json",
                "RequestVerificationToken": token.value
            },
            body: JSON.stringify({
                mode: button.dataset.mode,
                currentPath: input.value,
                title: button.dataset.title
            })
        });
        const result = await response.json();
        if (!response.ok) throw new Error(result.message || result.detail || "The path picker could not be opened.");
        if (result.path) {
            input.value = result.path;
            input.dispatchEvent(new Event("change", { bubbles: true }));
            showPathPickerMessage("Path selected", false);
        }
    } catch (error) {
        showPathPickerMessage(error.message, true);
    } finally {
        button.disabled = false;
        button.innerHTML = original;
    }
};

window.showPathPickerMessage = (message, isError) => {
    window.localGhostToast?.(isError ? "Path selection failed" : "Path selected", isError ? message : "Local path added to the field.", isError ? "error" : "success");
};
