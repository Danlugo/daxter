// Lightweight DAX autocomplete for a <textarea>. No external dependencies.
// Suggestions (fields + DAX functions) are supplied by Blazor via setCandidates.
window.daxComplete = (function () {
    const states = new WeakMap();

    function attach(el) {
        if (!el || states.get(el)) return;
        const box = document.createElement('div');
        box.className = 'dax-ac';
        box.style.display = 'none';
        document.body.appendChild(box);

        const s = { el, box, items: [], sel: 0, candidates: [], tokenStart: 0, tokenEnd: 0 };
        states.set(el, s);

        el.addEventListener('input', () => update(s));
        el.addEventListener('click', () => update(s));
        el.addEventListener('keydown', (e) => onKey(s, e));
        el.addEventListener('blur', () => setTimeout(() => hide(s), 150));
        window.addEventListener('resize', () => hide(s));
    }

    function setCandidates(el, list) {
        const s = states.get(el);
        if (s) s.candidates = list || [];
    }

    function tokenAt(text, pos) {
        let start = pos;
        while (start > 0 && /[A-Za-z0-9_]/.test(text[start - 1])) start--;
        return { start, word: text.slice(start, pos) };
    }

    function update(s) {
        const pos = s.el.selectionStart;
        const { start, word } = tokenAt(s.el.value, pos);
        if (word.length < 2) return hide(s);
        const w = word.toLowerCase();

        const matches = s.candidates
            .filter(c => c.label.toLowerCase().includes(w))
            .sort((a, b) => {
                const as = a.label.toLowerCase().startsWith(w) ? 0 : 1;
                const bs = b.label.toLowerCase().startsWith(w) ? 0 : 1;
                return as - bs || a.label.length - b.label.length;
            })
            .slice(0, 12);

        if (!matches.length) return hide(s);
        s.items = matches; s.sel = 0; s.tokenStart = start; s.tokenEnd = pos;
        render(s);
    }

    function icon(k) { return k === 'function' ? 'ƒ' : k === 'measure' ? '∑' : k === 'column' ? '▦' : '⊞'; }

    function render(s) {
        s.box.innerHTML = '';
        s.items.forEach((m, i) => {
            const div = document.createElement('div');
            div.className = 'dax-ac-item' + (i === s.sel ? ' sel' : '');
            const ic = document.createElement('span');
            ic.className = 'ac-kind ac-' + m.kind;
            ic.textContent = icon(m.kind);
            div.appendChild(ic);
            div.appendChild(document.createTextNode(m.label));
            div.addEventListener('mousedown', (e) => { e.preventDefault(); accept(s, m); });
            s.box.appendChild(div);
        });
        position(s);
        s.box.style.display = 'block';
    }

    function position(s) {
        const rect = s.el.getBoundingClientRect();
        const c = caretCoords(s.el, s.tokenStart);
        s.box.style.left = Math.round(rect.left + c.left) + 'px';
        s.box.style.top = Math.round(rect.top + c.top - s.el.scrollTop + 20) + 'px';
    }

    function caretCoords(el, position) {
        const style = getComputedStyle(el);
        const div = document.createElement('div');
        div.style.position = 'absolute';
        div.style.visibility = 'hidden';
        div.style.whiteSpace = 'pre-wrap';
        div.style.wordWrap = 'break-word';
        div.style.font = style.font;
        div.style.padding = style.padding;
        div.style.border = style.border;
        div.style.lineHeight = style.lineHeight;
        div.style.width = el.clientWidth + 'px';
        div.textContent = el.value.slice(0, position);
        const span = document.createElement('span');
        span.textContent = el.value.slice(position) || '.';
        div.appendChild(span);
        document.body.appendChild(div);
        const coords = { top: span.offsetTop, left: span.offsetLeft };
        document.body.removeChild(div);
        return coords;
    }

    function accept(s, m) {
        const v = s.el.value;
        const insert = m.insert || m.label;
        const before = v.slice(0, s.tokenStart);
        const after = v.slice(s.tokenEnd);
        s.el.value = before + insert + after;
        const caret = (before + insert).length;
        s.el.selectionStart = s.el.selectionEnd = caret;
        hide(s);
        s.el.dispatchEvent(new Event('input', { bubbles: true })); // sync Blazor @bind
        s.el.focus();
    }

    function onKey(s, e) {
        if (s.box.style.display === 'none') return;
        if (e.key === 'ArrowDown') { s.sel = (s.sel + 1) % s.items.length; render(s); e.preventDefault(); }
        else if (e.key === 'ArrowUp') { s.sel = (s.sel - 1 + s.items.length) % s.items.length; render(s); e.preventDefault(); }
        else if (e.key === 'Enter' || e.key === 'Tab') { accept(s, s.items[s.sel]); e.preventDefault(); }
        else if (e.key === 'Escape') { hide(s); e.preventDefault(); }
    }

    function hide(s) { if (s) s.box.style.display = 'none'; }

    return { attach, setCandidates };
})();
