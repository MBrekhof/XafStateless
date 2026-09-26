// Mermaid 11.17.2 (resolved from https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js, downloaded 2026-09-26).
// Exposes window.diagram: render(element, text, direction), zoom(element, factor), fit(element).
// Called from Diagram.razor through IJSRuntime; relies on the global `mermaid` from mermaid.min.js, loaded first.
//
// Theme: verified against the live DOM (not assumed) that XAF/DevExpress Blazor adds a "dxbl-theme-dark" class to
// document.body for every dark theme the switcher offers - both the Fluent Light/Dark toggle and a DevExpress
// Classic dark theme (e.g. Blazing Dark) set it the same way. Mermaid's default theme draws dark-grey lines that
// are unreadable on that dark background, so pick Mermaid's own "dark" theme to match, and re-render in place
// whenever the mode changes (a MutationObserver on body - no server round-trip needed). securityLevel is left
// unset, i.e. Mermaid's strict default.
//
// XSM-023: each diagram element keeps its own state (text, direction, zoom), so a theme change redraws every diagram on
// the page with its own orientation and zoom. Direction "LR" (landscape) adds Mermaid's top-level `direction LR` line to
// the engine's text; "TB" (portrait) is Mermaid's default. Zoom scales the drawn SVG from its natural width; null = fit.
window.diagram = (function () {
    const diagrams = new Map();   // element -> { text, direction, zoom }
    let initializedDark = null;   // null until the first render; tracks which mode mermaid.initialize last used
    let observing = false;
    let counter = 0;

    function isDark() {
        return document.body.classList.contains("dxbl-theme-dark");
    }

    function ensureInitialized(dark) {
        if (initializedDark === dark) return;
        initializedDark = dark;
        mermaid.initialize({ startOnLoad: false, theme: dark ? "dark" : "default" });
    }

    function observeThemeChanges() {
        if (observing) return;
        observing = true;
        new MutationObserver(() => {
            if (initializedDark === isDark()) return;   // another class changed, not the mode
            prune();
            for (const element of diagrams.keys()) draw(element);
        }).observe(document.body, { attributes: true, attributeFilter: ["class"] });
    }

    /// Forgets diagrams whose element left the page (a closed view), so their state and SVG are not kept alive.
    function prune() {
        for (const element of [...diagrams.keys()]) if (!element.isConnected) diagrams.delete(element);
    }

    function withDirection(text, direction) {
        if (direction !== "LR") return text;
        const newline = text.indexOf("\n");
        return newline < 0 ? text : text.slice(0, newline + 1) + "    direction LR\n" + text.slice(newline + 1);
    }

    function svgOf(element) {
        return element.querySelector("svg");
    }

    /// The SVG's natural width, from the max-width Mermaid writes, else its viewBox.
    function naturalWidth(svg) {
        if (!svg.dataset.naturalWidth) {
            const fromStyle = parseFloat(svg.style.maxWidth);
            svg.dataset.naturalWidth = String(fromStyle > 0 ? fromStyle : (svg.viewBox && svg.viewBox.baseVal ? svg.viewBox.baseVal.width : svg.getBoundingClientRect().width));
            svg.dataset.fitMaxWidth = svg.style.maxWidth;
        }
        return parseFloat(svg.dataset.naturalWidth);
    }

    function applyZoom(element) {
        const svg = svgOf(element);
        const state = diagrams.get(element);
        if (!svg || !state) return;
        const natural = naturalWidth(svg);
        if (state.zoom === null) {   // fit: Mermaid's own responsive sizing
            svg.style.width = "100%";
            svg.style.maxWidth = svg.dataset.fitMaxWidth;
        } else {
            svg.style.maxWidth = "none";   // the cap would stop zoom-in at the natural width
            svg.style.width = Math.round(natural * state.zoom) + "px";
        }
    }

    async function draw(element) {
        const state = diagrams.get(element);
        if (!state) return;
        if (!state.text) {
            element.innerHTML = "";
            return;
        }
        ensureInitialized(isDark());
        try {
            const { svg, bindFunctions } = await mermaid.render("xafstateless-diagram-" + (counter++), withDirection(state.text, state.direction));
            element.innerHTML = svg;
            if (bindFunctions) bindFunctions(element);
            applyZoom(element);
        } catch (e) {
            element.innerHTML = "";
            console.error("diagram.render failed", e);
        }
    }

    function render(element, text, direction) {
        if (!element) return;
        prune();
        const previous = diagrams.get(element);
        diagrams.set(element, { text, direction: direction || "TB", zoom: previous ? previous.zoom : null });
        observeThemeChanges();
        return draw(element);
    }

    /// Multiplies the current displayed scale by factor, up to 4x the natural size. Zooming out never enlarges: a large
    /// diagram fitted below 5% may still shrink from where it is.
    function zoom(element, factor) {
        const svg = svgOf(element);
        const state = diagrams.get(element);
        if (!svg || !state) return;
        const current = state.zoom !== null ? state.zoom : svg.getBoundingClientRect().width / naturalWidth(svg);
        state.zoom = Math.min(4, Math.max(Math.min(0.05, current), current * factor));
        applyZoom(element);
    }

    function fit(element) {
        const state = diagrams.get(element);
        if (!state) return;
        state.zoom = null;
        applyZoom(element);
    }

    return { render, zoom, fit };
})();
