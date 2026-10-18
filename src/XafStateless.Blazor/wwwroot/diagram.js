// beautiful-mermaid 1.1.3 (https://github.com/lukilabs/beautiful-mermaid, bundled into mermaid.min.js).
// Exposes window.diagram: render(element, text, direction), zoom(element, factor), fit(element).
// Called from Diagram.razor through IJSRuntime; relies on window.beautifulMermaid from mermaid.min.js, loaded first.
//
// Theme: verified against the live DOM (not assumed) that XAF/DevExpress Blazor adds a "dxbl-theme-dark" class to
// document.body for every dark theme the switcher offers - both the Fluent Light/Dark toggle and a DevExpress
// Classic dark theme (e.g. Blazing Dark) set it the same way. beautiful-mermaid derives its connector, arrowhead and
// label colors from --bg and --fg CSS custom properties on the <svg>, so a MutationObserver on document.body flips
// those variables in place whenever the mode changes (no re-layout or server round-trip needed). The Google Fonts
// @import that beautiful-mermaid emits in <style> is stripped so the diagram makes no external network requests.
//
// XSM-023: each diagram element keeps its own state (text, direction, zoom). Direction "LR" (landscape) adds
// Mermaid's top-level `direction LR` line to the engine's text; "TB" (portrait) is the default. Zoom scales the drawn
// SVG from its natural width; null = fit.
//
// XSM-027/028: beautiful-mermaid's stateDiagram-v2 parser ignores classDef/class lines, so applyWorkflowStyles merges
// them in declaration order onto each <g class="node" data-id="..."> (both its class list and its rect/text styles).
window.diagram = (function () {
    const diagrams = new Map();   // element -> { text, direction, zoom }
    let currentDark = null;
    let observing = false;

    function isDark() {
        return document.body.classList.contains("dxbl-theme-dark");
    }

    function themeColors(dark) {
        return dark ? { bg: "#18181b", fg: "#fafafa" } : { bg: "#ffffff", fg: "#27272a" };
    }

    function observeThemeChanges() {
        if (observing) return;
        observing = true;
        new MutationObserver(() => {
            const dark = isDark();
            if (currentDark === dark) return;   // another class changed, not the mode
            currentDark = dark;
            prune();
            const colors = themeColors(dark);
            for (const element of diagrams.keys()) {
                const svg = svgOf(element);
                if (svg) {
                    svg.style.setProperty("--bg", colors.bg);
                    svg.style.setProperty("--fg", colors.fg);
                }
            }
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

    /// Applies classDef/class statements from the Mermaid text onto beautiful-mermaid's <g class="node" data-id="...">
    /// groups in declaration order (kind -> problem -> current), so later styles win where they overlap and E2E/CSS
    /// selectors like `svg g.problem` and `svg g.current` match the styled state groups.
    function applyWorkflowStyles(svgText, mermaidText) {
        const classDefs = new Map();
        const nodeClasses = new Map();
        for (const rawLine of mermaidText.split("\n")) {
            const line = rawLine.trim();
            const defMatch = line.match(/^classDef\s+(\w+)\s+(.+)$/);
            if (defMatch) {
                const props = {};
                for (const pair of defMatch[2].replace(/;\s*$/, "").split(",")) {
                    const idx = pair.indexOf(":");
                    if (idx > 0) props[pair.slice(0, idx).trim()] = pair.slice(idx + 1).trim();
                }
                classDefs.set(defMatch[1], props);
                continue;
            }
            const clsMatch = line.match(/^class\s+([\w,-]+)\s+(\w+)$/);
            if (clsMatch) {
                for (const id of clsMatch[1].split(",").map(s => s.trim())) {
                    if (!nodeClasses.has(id)) nodeClasses.set(id, []);
                    nodeClasses.get(id).push(clsMatch[2]);
                }
            }
        }
        if (nodeClasses.size === 0) return svgText;
        return svgText.replace(
            /<g class="node" data-id="([^"]+)"([^>]*)>([\s\S]*?)<\/g>/g,
            (full, id, restAttrs, inner) => {
                const classes = nodeClasses.get(id);
                if (!classes || classes.length === 0) return full;
                const merged = {};
                for (const c of classes) Object.assign(merged, classDefs.get(c) || {});
                let updated = inner;
                if (merged.fill) updated = updated.replace(/(<rect\b[^>]*?\bfill=")[^"]*(")/, `$1${merged.fill}$2`);
                if (merged.stroke) updated = updated.replace(/(<rect\b[^>]*?\bstroke=")[^"]*(")/, `$1${merged.stroke}$2`);
                if (merged["stroke-width"]) updated = updated.replace(/(<rect\b[^>]*?\bstroke-width=")[^"]*(")/, `$1${merged["stroke-width"]}$2`);
                if (merged.color) updated = updated.replace(/(<text\b[^>]*?\bfill=")[^"]*(")/, `$1${merged.color}$2`);
                return `<g class="node ${classes.join(" ")}" data-id="${id}"${restAttrs}>${updated}</g>`;
            }
        );
    }

    function svgOf(element) {
        return element.querySelector("svg");
    }

    /// The SVG's natural width, from its width attribute or viewBox.
    function naturalWidth(svg) {
        if (!svg.dataset.naturalWidth) {
            const fromAttr = parseFloat(svg.getAttribute("width"));
            const fromViewBox = svg.viewBox && svg.viewBox.baseVal ? svg.viewBox.baseVal.width : 0;
            const natural = fromAttr > 0 ? fromAttr : (fromViewBox > 0 ? fromViewBox : svg.getBoundingClientRect().width);
            svg.dataset.naturalWidth = String(natural);
            svg.dataset.fitMaxWidth = natural + "px";
        }
        return parseFloat(svg.dataset.naturalWidth);
    }

    function applyZoom(element) {
        const svg = svgOf(element);
        const state = diagrams.get(element);
        if (!svg || !state) return;
        const natural = naturalWidth(svg);
        svg.style.height = "auto";
        if (state.zoom === null) {   // fit: responsive up to the natural width
            svg.style.width = "100%";
            svg.style.maxWidth = svg.dataset.fitMaxWidth;
        } else {
            svg.style.maxWidth = "none";   // the cap would stop zoom-in at the natural width
            svg.style.width = Math.round(natural * state.zoom) + "px";
        }
    }

    function draw(element) {
        const state = diagrams.get(element);
        if (!state) return;
        if (!state.text) {
            element.innerHTML = "";
            return;
        }
        if (typeof beautifulMermaid === "undefined" || typeof beautifulMermaid.renderMermaidSVG !== "function") {
            throw new Error("beautiful-mermaid (mermaid.min.js) is not loaded");
        }
        const dark = isDark();
        currentDark = dark;
        const colors = themeColors(dark);
        try {
            const source = withDirection(state.text, state.direction);
            const rawSvg = beautifulMermaid.renderMermaidSVG(source, {
                bg: colors.bg,
                fg: colors.fg,
                transparent: true,
                padding: 16,
                nodeSpacing: 20,
                layerSpacing: 28
            }).replace(/@import url\([^)]+\);/g, "");
            element.innerHTML = applyWorkflowStyles(rawSvg, state.text);
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
        draw(element);
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
