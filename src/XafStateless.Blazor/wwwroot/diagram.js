// Mermaid 11.17.2 (resolved from https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js, downloaded 2026-09-26).
// Exposes window.diagram.render(element, text): renders a Mermaid stateDiagram-v2 definition as inline SVG into element.
// Called from Diagram.razor through IJSRuntime; relies on the global `mermaid` from mermaid.min.js, loaded first.
//
// Theme: verified against the live DOM (not assumed) that XAF/DevExpress Blazor adds a "dxbl-theme-dark" class to
// document.body for every dark theme the switcher offers - both the Fluent Light/Dark toggle and a DevExpress
// Classic dark theme (e.g. Blazing Dark) set it the same way. Mermaid's default theme draws dark-grey lines that
// are unreadable on that dark background, so pick Mermaid's own "dark" theme to match, and re-render in place
// whenever that class toggles (a MutationObserver on body - no server round-trip needed). securityLevel is left
// unset, i.e. Mermaid's strict default.
window.diagram = (function () {
    let initializedDark = null;   // null until the first render; tracks which mode mermaid.initialize last used
    let last = null;              // { element, text } of the most recent render, replayed when the theme toggles
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
            if (last && last.element.isConnected) render(last.element, last.text);
        }).observe(document.body, { attributes: true, attributeFilter: ["class"] });
    }

    async function render(element, text) {
        if (!element) return;
        last = { element, text };
        observeThemeChanges();
        if (!text) {
            element.innerHTML = "";
            return;
        }
        ensureInitialized(isDark());
        try {
            const { svg, bindFunctions } = await mermaid.render("xafstateless-diagram-" + (counter++), text);
            element.innerHTML = svg;
            if (bindFunctions) bindFunctions(element);
        } catch (e) {
            element.innerHTML = "";
            console.error("diagram.render failed", e);
        }
    }

    return { render };
})();
