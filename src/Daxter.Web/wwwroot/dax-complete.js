// Lightweight DAX autocomplete + signature help for a <textarea>. No external dependencies.
// Field suggestions (tables/columns/measures) are supplied by Blazor via setCandidates;
// DAX function signatures are static below.
window.daxComplete = (function () {
    const states = new WeakMap();

    // function -> ordered parameter names (… = repeatable).
    const SIGS = {
        CALCULATE:["Expression","Filter1","Filter2","…"], CALCULATETABLE:["Table","Filter1","…"],
        FILTER:["Table","FilterExpression"], ALL:["TableOrColumn","…"], ALLEXCEPT:["Table","Column1","…"],
        ALLSELECTED:["TableOrColumn"], REMOVEFILTERS:["TableOrColumn","…"], KEEPFILTERS:["Expression"],
        VALUES:["TableOrColumn"], DISTINCT:["Column"], RELATED:["Column"], RELATEDTABLE:["Table"],
        SELECTEDVALUE:["ColumnName","AlternateResult"], HASONEVALUE:["ColumnName"],
        SUM:["Column"], SUMX:["Table","Expression"], AVERAGE:["Column"], AVERAGEX:["Table","Expression"],
        MIN:["ColumnOrValue"], MINX:["Table","Expression"], MAX:["ColumnOrValue"], MAXX:["Table","Expression"],
        COUNT:["Column"], COUNTA:["Column"], COUNTAX:["Table","Expression"], COUNTX:["Table","Expression"],
        COUNTROWS:["Table"], COUNTBLANK:["Column"], DISTINCTCOUNT:["Column"],
        DIVIDE:["Numerator","Denominator","AlternateResult"], ROUND:["Number","NumDigits"],
        IF:["LogicalTest","ResultIfTrue","ResultIfFalse"], IFERROR:["Value","ValueIfError"],
        SWITCH:["Expression","Value1","Result1","…","Else"], AND:["Logical1","Logical2"], OR:["Logical1","Logical2"],
        NOT:["Logical"], ISBLANK:["Value"], COALESCE:["Value1","Value2","…"],
        CONCATENATE:["Text1","Text2"], CONCATENATEX:["Table","Expression","Delimiter"], FORMAT:["Value","FormatString"],
        LEFT:["Text","NumChars"], RIGHT:["Text","NumChars"], MID:["Text","Start","NumChars"], LEN:["Text"],
        SEARCH:["FindText","WithinText","StartPos","NotFoundValue"],
        DATE:["Year","Month","Day"], YEAR:["Date"], MONTH:["Date"], DAY:["Date"],
        DATEDIFF:["Date1","Date2","Interval"], DATEADD:["Dates","NumberOfIntervals","Interval"], EDATE:["Date","Months"],
        TOTALYTD:["Expression","Dates","Filter","YearEndDate"], TOTALMTD:["Expression","Dates","Filter"], TOTALQTD:["Expression","Dates","Filter"],
        SAMEPERIODLASTYEAR:["Dates"], PARALLELPERIOD:["Dates","NumberOfIntervals","Interval"],
        DATESYTD:["Dates","YearEndDate"], DATESINPERIOD:["Dates","StartDate","NumberOfIntervals","Interval"],
        DATESBETWEEN:["Dates","StartDate","EndDate"], FIRSTDATE:["Dates"], LASTDATE:["Dates"],
        RANKX:["Table","Expression","Value","Order","Ties"], TOPN:["N","Table","OrderBy1","Order1","…"],
        ROW:["Name1","Expression1","…"], SUMMARIZE:["Table","GroupBy1","…","Name1","Expression1"],
        SUMMARIZECOLUMNS:["GroupBy1","…","Filter","Name1","Expression1"], ADDCOLUMNS:["Table","Name1","Expression1","…"],
        SELECTCOLUMNS:["Table","Name1","Expression1","…"], CROSSJOIN:["Table1","Table2","…"], UNION:["Table1","Table2","…"],
        TREATAS:["TableExpression","Column1","…"], USERELATIONSHIP:["Column1","Column2"], CROSSFILTER:["Column1","Column2","Direction"],
        LOOKUPVALUE:["ResultColumn","SearchColumn1","SearchValue1","…"], CONTAINS:["Table","Column1","Value1","…"],
    };

    // Short one-line descriptions (shown under the signature).
    const DESC = {
        CALCULATE:"Evaluate an expression in a modified filter context.", CALCULATETABLE:"Evaluate a table expression in a modified filter context.",
        FILTER:"Return a table filtered by a boolean expression.", ALL:"Remove filters from a table or columns.",
        ALLEXCEPT:"Remove all filters from a table except the given columns.", ALLSELECTED:"Outer filter context, keeping explicit selections.",
        REMOVEFILTERS:"Clear filters from the given table or columns.", KEEPFILTERS:"Add filters without overwriting existing ones.",
        VALUES:"Distinct values of a column (or table rows) in context.", DISTINCT:"Distinct values of a column.",
        RELATED:"Fetch a related value across a relationship (many-to-one).", RELATEDTABLE:"Related rows across a relationship (one-to-many).",
        SELECTEDVALUE:"The single value of a column, or a fallback.", HASONEVALUE:"TRUE if a column has exactly one value in context.",
        SUM:"Sum a column.", SUMX:"Sum an expression evaluated per row of a table.", AVERAGE:"Average a column.", AVERAGEX:"Average an expression per row.",
        MIN:"Minimum of a column or two values.", MINX:"Minimum of an expression per row.", MAX:"Maximum of a column or two values.", MAXX:"Maximum of an expression per row.",
        COUNT:"Count non-blank numeric/date values.", COUNTROWS:"Count rows of a table.", COUNTA:"Count non-blank values.", DISTINCTCOUNT:"Count distinct values of a column.",
        DIVIDE:"Safe division (blank/alternate on divide-by-zero).", IF:"Return one value if true, another if false.", IFERROR:"A value, or an alternate if it errors.",
        SWITCH:"Match an expression against values and return the match.", COALESCE:"First non-blank argument.",
        CONCATENATEX:"Concatenate an expression over a table with a delimiter.", FORMAT:"Format a value as text using a format string.",
        DATEDIFF:"Difference between two dates in the given interval.", DATEADD:"Shift dates by an interval (time intelligence).",
        TOTALYTD:"Year-to-date total of an expression.", SAMEPERIODLASTYEAR:"Shift dates back one year.",
        DATESINPERIOD:"Dates in a period relative to a start date.", DATESBETWEEN:"Dates between a start and end date.",
        RANKX:"Rank rows of a table by an expression.", TOPN:"Top N rows ordered by an expression.",
        SUMMARIZECOLUMNS:"Group by columns with filters + measures (query table).", ADDCOLUMNS:"Add calculated columns to a table.",
        SELECTCOLUMNS:"Project / rename columns of a table.", TREATAS:"Apply a table's values as filters on columns.",
        USERELATIONSHIP:"Activate an inactive relationship inside CALCULATE.", LOOKUPVALUE:"Return a value by matching search columns.",
    };

    function docsUrl(name) {
        return "https://learn.microsoft.com/en-us/dax/" + name.toLowerCase().replace(/\./g, "-") + "-function-dax";
    }

    function attach(el) {
        if (!el || states.get(el)) return;
        const box = document.createElement('div'); box.className = 'dax-ac'; box.style.display = 'none';
        const sig = document.createElement('div'); sig.className = 'dax-sig'; sig.style.display = 'none';
        document.body.appendChild(box); document.body.appendChild(sig);

        const s = { el, box, sig, items: [], sel: 0, candidates: [], tokenStart: 0, tokenEnd: 0 };
        states.set(el, s);

        const activity = () => { update(s); updateSig(s); };
        el.addEventListener('input', activity);
        el.addEventListener('click', activity);
        el.addEventListener('keyup', (e) => { if (['ArrowLeft','ArrowRight','Home','End'].includes(e.key)) activity(); });
        el.addEventListener('keydown', (e) => onKey(s, e));
        el.addEventListener('blur', () => setTimeout(() => { hide(s); s.sig.style.display = 'none'; }, 150));
        window.addEventListener('resize', () => { hide(s); s.sig.style.display = 'none'; });
    }

    function setCandidates(el, list) { const s = states.get(el); if (s) s.candidates = list || []; }

    function tokenAt(text, pos) {
        let start = pos;
        while (start > 0 && /[A-Za-z0-9_]/.test(text[start - 1])) start--;
        return { start, word: text.slice(start, pos) };
    }

    // The function call the caret is currently inside, with the active argument index.
    function activeCall(text, pos) {
        let depth = 0, arg = 0, i = pos - 1;
        while (i >= 0) {
            const c = text[i];
            if (c === ')') depth++;
            else if (c === '(') {
                if (depth === 0) {
                    let j = i - 1; while (j >= 0 && /\s/.test(text[j])) j--;
                    const end = j + 1; while (j >= 0 && /[A-Za-z0-9_.]/.test(text[j])) j--;
                    const name = text.slice(j + 1, end).toUpperCase();
                    return name ? { name, arg } : null;
                }
                depth--;
            } else if (c === ',' && depth === 0) arg++;
            i--;
        }
        return null;
    }

    function paramKind(p) {
        p = (p || '').toLowerCase();
        if (p.includes('table')) return 'table';
        if (p.includes('measure')) return 'measure';
        if (p.includes('column') || p.includes('name') || p.includes('dates')) return 'column';
        if (p.includes('expression') || p.includes('value')) return 'expr';
        return null;
    }

    function expectedKind(s, pos) {
        const call = activeCall(s.el.value, pos);
        if (!call || !SIGS[call.name]) return null;
        const params = SIGS[call.name];
        const idx = Math.min(call.arg, params.length - 1);
        return paramKind(params[idx]);
    }

    function update(s) {
        const pos = s.el.selectionStart;
        const { start, word } = tokenAt(s.el.value, pos);
        if (word.length < 2) return hide(s);
        const w = word.toLowerCase();
        const want = expectedKind(s, pos); // bias field kinds toward what the argument expects

        const matches = s.candidates
            .filter(c => c.label.toLowerCase().includes(w))
            .sort((a, b) => score(a, w, want) - score(b, w, want) || a.label.length - b.label.length)
            .slice(0, 12);

        if (!matches.length) return hide(s);
        s.items = matches; s.sel = 0; s.tokenStart = start; s.tokenEnd = pos;
        render(s);
    }

    function score(c, w, want) {
        let n = c.label.toLowerCase().startsWith(w) ? 0 : 2;
        if (want) {
            const k = c.kind;
            const ok = want === 'expr' ? (k === 'measure' || k === 'column') : k === want;
            if (ok) n -= 1; // boost expected kinds
        }
        return n;
    }

    function icon(k) { return k === 'function' ? 'ƒ' : k === 'keyword' ? '▪' : k === 'measure' ? '∑' : k === 'column' ? '▦' : '⊞'; }

    function render(s) {
        s.box.innerHTML = '';
        s.items.forEach((m, i) => {
            const div = document.createElement('div');
            div.className = 'dax-ac-item' + (i === s.sel ? ' sel' : '');
            const ic = document.createElement('span'); ic.className = 'ac-kind ac-' + m.kind; ic.textContent = icon(m.kind);
            div.appendChild(ic);
            const lab = document.createElement('span'); lab.className = 'ac-label'; lab.textContent = m.label;
            div.appendChild(lab);
            if (m.kind === 'function') {
                const a = document.createElement('a');
                a.className = 'ac-docs'; a.textContent = '↗'; a.title = 'Open on Microsoft Learn';
                a.href = docsUrl(m.label); a.target = '_blank'; a.rel = 'noopener noreferrer';
                a.addEventListener('mousedown', (e) => e.stopPropagation()); // don't insert
                div.appendChild(a);
            }
            div.addEventListener('mousedown', (e) => { e.preventDefault(); accept(s, m); });
            s.box.appendChild(div);
        });
        const c = caretCoords(s.el, s.tokenStart);
        const r = s.el.getBoundingClientRect();
        s.box.style.left = Math.round(r.left + c.left) + 'px';
        s.box.style.top = Math.round(r.top + c.top - s.el.scrollTop + 20) + 'px';
        s.box.style.display = 'block';
    }

    function updateSig(s) {
        const pos = s.el.selectionStart;
        const call = activeCall(s.el.value, pos);
        if (!call || !SIGS[call.name]) { s.sig.style.display = 'none'; return; }
        const params = SIGS[call.name];
        let active = call.arg;
        if (active > params.length - 1) active = params.includes('…') ? params.indexOf('…') : params.length - 1;
        const parts = params.map((p, i) => i === active ? '<b>' + p + '</b>' : p);
        let html = '<div class="sig-line"><span class="sig-fn">' + call.name + '</span>(' + parts.join(', ') + ')'
            + '<a class="sig-docs" href="' + docsUrl(call.name) + '" target="_blank" rel="noopener noreferrer" title="Microsoft Learn">↗ docs</a></div>';
        if (DESC[call.name]) html += '<div class="sig-desc">' + DESC[call.name] + '</div>';
        s.sig.innerHTML = html;
        const c = caretCoords(s.el, pos);
        const r = s.el.getBoundingClientRect();
        s.sig.style.left = Math.round(r.left + c.left) + 'px';
        s.sig.style.top = Math.round(r.top + c.top - s.el.scrollTop - 6) + 'px';
        s.sig.style.display = 'block';
    }

    function caretCoords(el, position) {
        const style = getComputedStyle(el);
        const div = document.createElement('div');
        Object.assign(div.style, {
            position: 'absolute', visibility: 'hidden', whiteSpace: 'pre-wrap', wordWrap: 'break-word',
            font: style.font, padding: style.padding, border: style.border, lineHeight: style.lineHeight,
            width: el.clientWidth + 'px',
        });
        div.textContent = el.value.slice(0, position);
        const span = document.createElement('span'); span.textContent = el.value.slice(position) || '.';
        div.appendChild(span); document.body.appendChild(div);
        const coords = { top: span.offsetTop, left: span.offsetLeft };
        document.body.removeChild(div);
        return coords;
    }

    function accept(s, m) {
        const v = s.el.value, insert = m.insert || m.label;
        const before = v.slice(0, s.tokenStart), after = v.slice(s.tokenEnd);
        s.el.value = before + insert + after;
        const caret = (before + insert).length;
        s.el.selectionStart = s.el.selectionEnd = caret;
        hide(s);
        s.el.dispatchEvent(new Event('input', { bubbles: true }));
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
