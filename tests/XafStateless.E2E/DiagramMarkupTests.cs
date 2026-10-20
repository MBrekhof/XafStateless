using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using XafStateless.Engine;

namespace XafStateless.E2E;

/// <summary>Markup in a caption, trigger or role draws as text, never as live HTML (an &lt;img&gt; in a caption loaded in
/// the browser with 0.2.0's Mermaid HTML labels). Loads the shipped diagram scripts into a blank page, so it needs no demo.</summary>
public class DiagramMarkupTests : PageTest
{
    static readonly string WwwRoot = Path.Combine(RepoRoot(), "src", "XafStateless.Blazor", "wwwroot");

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "XafStateless.slnx"))) return dir.FullName;
        throw new InvalidOperationException("XafStateless.slnx not found above " + AppContext.BaseDirectory);
    }

    [TestCase(false, TestName = "Diagram_draws_markup_as_text_light")]
    [TestCase(true, TestName = "Diagram_draws_markup_as_text_dark")]
    public async Task Diagram_draws_markup_as_text(bool dark)
    {
        // An http origin, so a protocol-relative src (captions cannot hold ':') resolves to a request the route can see.
        const string Origin = "http://diagram.test/";
        var requests = new List<string>();
        await Page.RouteAsync("**/*", async route =>
        {
            if (route.Request.Url == Origin)
                await route.FulfillAsync(new() { ContentType = "text/html", Body = "<!doctype html><html><body><div id=\"d\"></div></body></html>" });
            else
            {
                requests.Add(route.Request.Url);
                await route.AbortAsync();
            }
        });
        await Page.GotoAsync(Origin);
        await Page.AddScriptTagAsync(new() { Path = Path.Combine(WwwRoot, "mermaid.min.js") });
        await Page.AddScriptTagAsync(new() { Path = Path.Combine(WwwRoot, "diagram.js") });
        if (dark) await Page.EvaluateAsync("() => document.body.classList.add('dxbl-theme-dark')");

        var def = new WorkflowDef("markup", 1, "draft",
            [
                new StateDef("draft", "<img src=//example.invalid/pixel.png>Draft", StateKind.Task, TaskRole: "<img src=//example.invalid/role.png>",
                    Locked: ["<img src=//example.invalid/lock.png>"]),
                new StateDef("big", "Amount > 1000", StateKind.Decision),
                // Entity-encoded markup must end up as text too (the renderer decodes entities before escaping).
                new StateDef("encoded", "&lt;img src=//example.invalid/entity.png&gt;Encoded", StateKind.Manual),
                new StateDef("done", "Done", StateKind.Final),
            ],
            [
                new TransitionDef("draft", "<img src=//example.invalid/trigger.png>Submit", "big"),
                new TransitionDef("big", "Close", "encoded", Criteria: "<img src=//example.invalid/criteria.png>",
                    RequiredRole: "&#60;img src=//example.invalid/numeric.png&#62;"),
                new TransitionDef("encoded", "Finish", "done"),
            ]);
        var text = WorkflowMermaid.Format(def, new DiagramStyle(ByKind: true, Details: true));

        // render is synchronous since beautiful-mermaid and returned a promise with Mermaid 11; awaiting covers both.
        await Page.EvaluateAsync("async t => await window.diagram.render(document.getElementById('d'), t, 'TB')", text);

        var d = Page.Locator("#d");
        Assert.That(await d.Locator("svg").CountAsync(), Is.EqualTo(1), "the diagram did not draw");
        Assert.That(await d.Locator("img").CountAsync(), Is.Zero, "markup in a label became an <img>");
        Assert.That(requests.Where(u => u.Contains("example.invalid")), Is.Empty, "a label made the browser fetch a URL");
        // Labels are SVG text: no HTML island (Mermaid 11's HTML labels lived in foreignObject) and no script.
        Assert.That(await d.Locator("foreignObject, script").CountAsync(), Is.Zero, "a label was drawn as HTML");
        var shown = await d.TextContentAsync() ?? "";
        // Every payload reached the drawing as text, so the checks above were not passed by a label that went missing.
        // The entity-encoded ones never become entities: WorkflowMermaid.Escape turns their ';' into a space.
        foreach (var expected in new[]
                 {
                     "<img src=//example.invalid/pixel.png>Draft", "<img src=//example.invalid/role.png>",
                     "locks <img src=//example.invalid/lock.png>", "&lt img src=//example.invalid/entity.png&gt Encoded",
                     "<img src=//example.invalid/trigger.png>Submit", "[<img src=//example.invalid/criteria.png>]",
                     "(&#60 img src=//example.invalid/numeric.png&#62 )", "Amount > 1000",
                 })
            Assert.That(shown, Does.Contain(expected));
    }
}
