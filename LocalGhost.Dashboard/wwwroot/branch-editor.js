(() => {
    const split = value => (value || "")
        .split(/[,;\r\n]+/)
        .map(branch => branch.trim())
        .filter(Boolean);

    function render(editor) {
        const hidden = editor.querySelector('input[name="branch"]');
        const list = editor.querySelector('.branch-chips');
        if (!hidden || !list) return;

        const branches = [];
        for (const branch of split(hidden.value)) {
            if (!branches.some(existing => existing.toLowerCase() === branch.toLowerCase())) branches.push(branch);
        }
        if (!branches.length) branches.push("main");
        const value = branches.join(",");
        hidden.value = value;
        if (editor.dataset.renderedValue === value && list.querySelector('.branch-chip-remove')) return;
        editor.dataset.renderedValue = value;

        list.replaceChildren(...branches.map(branch => {
            const chip = document.createElement('span');
            chip.append(document.createTextNode(branch));
            const remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'branch-chip-remove';
            remove.dataset.branch = branch;
            remove.title = `Remove ${branch}`;
            remove.setAttribute('aria-label', `Remove ${branch}`);
            remove.textContent = '×';
            remove.disabled = branches.length === 1;
            chip.append(remove);
            return chip;
        }));
    }

    function add(editor, focus = true) {
        const input = editor.querySelector('.branch-entry');
        const hidden = editor.querySelector('input[name="branch"]');
        if (!input || !hidden || !input.value.trim()) return;

        const branches = split(hidden.value);
        for (const branch of split(input.value)) {
            if (!branches.some(existing => existing.toLowerCase() === branch.toLowerCase())) branches.push(branch);
        }
        hidden.value = branches.join(',');
        input.value = '';
        render(editor);
        if (focus) input.focus();
    }

    document.addEventListener('click', event => {
        if (!(event.target instanceof Element)) return;
        const addButton = event.target.closest('.branch-add');
        if (addButton) {
            const editor = addButton.closest('.branch-editor');
            if (editor) add(editor);
            return;
        }

        const removeButton = event.target.closest('.branch-chip-remove');
        if (!removeButton) return;
        const editor = removeButton.closest('.branch-editor');
        const hidden = editor?.querySelector('input[name="branch"]');
        if (!hidden) return;
        const branches = split(hidden.value);
        if (branches.length <= 1) return;
        hidden.value = branches.filter(branch => branch !== removeButton.dataset.branch).join(',');
        render(editor);
    });

    document.addEventListener('keydown', event => {
        if (!(event.target instanceof Element) || !event.target.matches('.branch-entry')) return;
        if (event.key !== 'Enter' && event.key !== ',') return;
        event.preventDefault();
        const editor = event.target.closest('.branch-editor');
        if (editor) add(editor);
    });

    document.addEventListener('submit', event => {
        if (event.target instanceof HTMLFormElement) {
            event.target.querySelectorAll('.branch-editor').forEach(editor => add(editor, false));
        }
    }, true);

    const renderAll = () => document.querySelectorAll('.branch-editor').forEach(render);
    const start = () => {
        renderAll();
        new MutationObserver(renderAll).observe(document.body, { childList: true, subtree: true });
    };
    if (document.body) start();
    else document.addEventListener('DOMContentLoaded', start, { once: true });
})();
