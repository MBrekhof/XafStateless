// Mermaid 11.17.2 (resolved from https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js, downloaded 2026-09-26).
// Exposes window.diagram.render(element, text): renders a Mermaid stateDiagram-v2 definition as inline SVG into element.
// Called from Diagram.razor through IJSRuntime; relies on the global `mermaid` from mermaid.min.js, loaded first.
window.diagram = (function () {
    let initialized = false;
    let counter = 0;

    function ensureInitialized() {
        if (!initialized) {
            mermaid.initialize({ startOnLoad: false });
            initialized = true;
        }
    }

    return {
        render: async function (element, text) {
            if (!element) return;
            if (!text) {
                element.innerHTML = "";
                return;
            }
            ensureInitialized();
            try {
                const { svg, bindFunctions } = await mermaid.render("xafstateless-diagram-" + (counter++), text);
                element.innerHTML = svg;
                if (bindFunctions) bindFunctions(element);
            } catch (e) {
                element.innerHTML = "";
                console.error("diagram.render failed", e);
            }
        }
    };
})();
