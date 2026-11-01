using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace XafStateless.E2E;

public sealed class E2ESettings
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string Designer { get; set; } = "designer";
    public string Approver { get; set; } = "approver";
    public string Password { get; set; } = "";
    public float TimeoutMs { get; set; } = 20000;
    public string ScreenshotDir { get; set; } = "screenshots";

    public static readonly E2ESettings Instance = Load();

    static E2ESettings Load()
    {
        var s = new E2ESettings();
        new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.e2e.json").Build().Bind(s);
        return s;
    }
}

/// <summary>The designer journey against the running demo (see README "How to run"). Needs a freshly seeded database:
/// it creates v2 of purchase-request, which only works while v1 is the latest version.
/// Navigation is by clicks after the one login page load per user; hard navigations race XAF's circuit.</summary>
public class DesignerJourneyTests : PageTest
{
    static E2ESettings S => E2ESettings.Instance;
    static readonly string V1Request = "Desk (v1)", V2Request = "Chair (v2)";

    public override BrowserNewContextOptions ContextOptions() => new() { ViewportSize = new() { Width = 1440, Height = 900 } };

    [Test, Order(1)]   // first: it needs v1 to be the latest version, and the approver's session unburdened by other tests' restored tabs
    public async Task Designer_adds_a_finance_step_and_each_request_follows_its_own_version()
    {
        SetDefaultExpectTimeout(S.TimeoutMs);

        // 0. Before v2 exists, the approver starts a request on v1.
        var approver = await LogIn(await (await Browser.NewContextAsync(ContextOptions())).NewPageAsync(), S.Approver);
        await NewRequest(approver, V1Request, 50);
        await Choose(approver, "Start workflow", "purchase-request");
        await Choose(approver, "Transition", "Submit");
        await Expect(WorkflowLink(approver, "Approval")).ToBeVisibleAsync();
        await Shot(approver, "00-v1-request-submitted");

        // 1. The designer runs New version on purchase-request: v2.
        var designer = await LogIn(Page, S.Designer);
        await Nav(designer, "Workflow", "Workflow Definition");
        await Row(designer, "purchase-request", "Published").GetByRole(AriaRole.Gridcell, new() { Name = "purchase-request", Exact = true }).ClickAsync();
        await Expect(designer.GetByRole(AriaRole.Button, new() { Name = "New version", Exact = true })).ToBeEnabledAsync();
        await Button(designer, "New version").ClickAsync();
        await Expect(designer.GetByRole(AriaRole.Spinbutton, new() { Name = "Version" })).ToHaveValueAsync("2");
        await Shot(designer, "01-new-version-v2");

        // 2. Add the Task state 'finance' for Approvers, and re-route approval --Approve--> finance.
        await designer.GetByRole(AriaRole.Tab, new() { Name = "States", Exact = true }).ClickAsync();
        await designer.GetByRole(AriaRole.Tabpanel, new() { Name = "States" }).GetByRole(AriaRole.Button, new() { Name = "New", Exact = true }).ClickAsync();
        await designer.GetByRole(AriaRole.Textbox, new() { Name = "Key*" }).FillAsync("finance");
        await designer.GetByRole(AriaRole.Textbox, new() { Name = "Caption*" }).FillAsync("Finance");
        await Pick(designer, "Kind", "Task");
        await Pick(designer, "Task Role", "Approvers");
        await Shot(designer, "02a-state-finance");

        // 2b. A Notify effect on the finance state; every effect editor is found by role and accessible name (XSM-019).
        await Button(designer, "New").ClickAsync();   // the Effects group's New: the only one on the state view
        await Expect(SelectedViewTab(designer)).ToContainTextAsync("Workflow Effect");   // the effect opens in its own tab
        await Shot(designer, "02b-effect-new");   // also shows the Workflow navigation images (XSM-020)
        await Pick(designer, "Kind", "Set field");   // XAF's sentence-case enum caption for EffectKind.SetField (XSM-020)
        await Pick(designer, "Kind", "Increment");   // XSM-031: the counter step renders, and its Target offers the numeric members
        await Pick(designer, "Target*", "Amount");
        await Pick(designer, "Kind", "Run action");   // XSM-032: the host's registered actions are the Target list
        await Pick(designer, "Target*", "log");
        await Pick(designer, "Kind", "Notify");
        await Pick(designer, "Target*", "Approvers");
        await designer.GetByRole(AriaRole.Spinbutton, new() { Name = "Order", Exact = true }).FillAsync("0");
        await designer.GetByRole(AriaRole.Textbox, new() { Name = "Value", Exact = true }).FillAsync("Finance: please check {Title}");
        await Shot(designer, "02c-effect-notify");
        await SaveAndCloseTab(designer, "Finance");
        await CloseSavedTab(designer);   // saving the effect from its own tab saved the new finance state with it

        await designer.GetByRole(AriaRole.Tab, new() { Name = "Transitions", Exact = true }).ClickAsync();
        await Row(designer, "approval", "Approve").GetByRole(AriaRole.Gridcell, new() { Name = "Approve", Exact = true }).ClickAsync();
        await Pick(designer, "To Key*", "finance");
        await SaveAndCloseTab(designer);
        await Refresh(designer);
        await Expect(Row(designer, "approval", "Approve").GetByRole(AriaRole.Gridcell, new() { Name = "finance", Exact = true })).ToBeVisibleAsync();
        await Shot(designer, "02b-approve-rerouted-to-finance");

        // 3. While finance has no way out, the definition shows it (XSM-027): Publish and Dry run are disabled with the reason
        //    as tooltip, the finance row is colored with its problem, and the diagram draws finance in red. With the edge
        //    added, Publish is enabled and succeeds. (Edits saved in their own tabs reach the definition view on Refresh.)
        await Refresh(designer);
        await Expect(Button(designer, "Publish")).ToBeDisabledAsync();
        await Expect(Button(designer, "Publish")).ToHaveAttributeAsync("title", new Regex("^Fix first: "));   // the first finding (here decide-amount, unreachable while finance is a dead end)
        await Expect(Button(designer, "Dry run")).ToBeDisabledAsync();
        await designer.GetByRole(AriaRole.Tab, new() { Name = "States", Exact = true }).ClickAsync();
        var problem = designer.GetByRole(AriaRole.Gridcell, new() { NameRegex = new Regex("^'finance' has no way out") });
        await Expect(problem).ToBeVisibleAsync();
        Assert.That(await Row(designer, "finance", "Finance").GetByRole(AriaRole.Gridcell, new() { Name = "Finance", Exact = true })
            .EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor"), Is.EqualTo("rgb(253, 226, 226)"));
        await Expect(designer.Locator(".xafstateless-diagram-root:visible svg g.problem").Filter(new() { HasTextString = "Finance" })).ToHaveCountAsync(1);
        await Shot(designer, "03a-publish-disabled-finance-problem");

        await designer.GetByRole(AriaRole.Tab, new() { Name = "Transitions", Exact = true }).ClickAsync();
        await designer.GetByRole(AriaRole.Tabpanel, new() { Name = "Transitions" }).GetByRole(AriaRole.Button, new() { Name = "New", Exact = true }).ClickAsync();
        await Pick(designer, "From Key*", "finance");
        await designer.GetByRole(AriaRole.Textbox, new() { Name = "Action name*" }).FillAsync("Approve");   // the Trigger, relabelled (XSM-022)
        await Pick(designer, "To Key*", "decide-amount");
        await SaveAndCloseTab(designer);
        await Refresh(designer);
        await Expect(Button(designer, "Publish")).ToBeEnabledAsync();
        await Expect(designer.Locator(".xafstateless-diagram-root:visible svg g.problem")).ToHaveCountAsync(0);
        await Button(designer, "Publish").ClickAsync();
        await Expect(Alert(designer, "'purchase-request' v2 published.")).ToBeVisibleAsync();
        await Expect(designer.GetByRole(AriaRole.Combobox, new() { Name = "State", Exact = true })).ToHaveValueAsync("Published");
        await Expect(designer.GetByRole(AriaRole.Combobox, new() { Name = "Target Type Name*", Exact = true })).ToBeDisabledAsync();   // frozen once published
        await Shot(designer, "03b-published-v2");

        // 4. The diagram, drawn from the same definition, shows finance.
        await Refresh(designer);
        var diagram = designer.Locator(".xafstateless-diagram-root:visible");
        await Expect(diagram.Locator("svg").First).ToContainTextAsync("Finance");
        await Expect(designer.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);   // the publish toast auto-closes; keep it off the picture
        await designer.SetViewportSizeAsync(1440, 1600);                        // the whole v2 graph, not clipped by the view's scroll area
        await diagram.ScrollIntoViewIfNeededAsync();
        await diagram.ScreenshotAsync(new() { Path = ShotPath("04-diagram-with-finance"), Animations = ScreenshotAnimations.Disabled });
        TestContext.AddTestAttachment(ShotPath("04-diagram-with-finance"));

        // 4b. The diagram toolbar (XSM-023): landscape draws left to right, zoom scales it, Fit and Portrait restore it.
        var svg = diagram.Locator("svg").First;
        async Task<LocatorBoundingBoxResult> Box() => (await svg.BoundingBoxAsync())!;
        var portrait = await Box();
        await diagram.GetByRole(AriaRole.Button, new() { Name = "Show left to right" }).ClickAsync();
        await Expect(diagram.GetByRole(AriaRole.Button, new() { Name = "Show top to bottom" })).ToBeVisibleAsync();
        await Until("landscape is wider than portrait", async () => { var b = await Box(); return b.Width / b.Height > portrait.Width / portrait.Height; });
        await Expect(svg).ToContainTextAsync("Finance");
        var fitted = await Box();
        await diagram.GetByRole(AriaRole.Button, new() { Name = "Zoom in" }).ClickAsync();
        await Until("zoom in enlarges", async () => (await Box()).Width > fitted.Width * 1.1);
        await diagram.ScreenshotAsync(new() { Path = ShotPath("04b-diagram-landscape-zoomed"), Animations = ScreenshotAnimations.Disabled });
        await diagram.GetByRole(AriaRole.Button, new() { Name = "Fit to width" }).ClickAsync();
        await Until("fit restores the width", async () => Math.Abs((await Box()).Width - fitted.Width) < 2);
        await diagram.ScreenshotAsync(new() { Path = ShotPath("04c-diagram-landscape"), Animations = ScreenshotAnimations.Disabled });   // README picture
        await diagram.GetByRole(AriaRole.Button, new() { Name = "Show top to bottom" }).ClickAsync();
        await Expect(diagram.GetByRole(AriaRole.Button, new() { Name = "Show left to right" })).ToBeVisibleAsync();
        await Until("portrait is drawn again", async () => { var b = await Box(); return Math.Abs(b.Width / b.Height - portrait.Width / portrait.Height) < 0.05; });
        await Expect(svg).ToContainTextAsync("Finance");
        await designer.SetViewportSizeAsync(1440, 900);

        // 5. The approver creates a request (Amount 50), starts it (now on v2) and submits it.
        await NewRequest(approver, V2Request, 50);
        await Choose(approver, "Start workflow", "purchase-request");
        await Choose(approver, "Transition", "Submit");
        await Expect(WorkflowLink(approver, "Approval")).ToBeVisibleAsync();
        await Shot(approver, "05-v2-request-submitted");

        // 6. Approve the approval task, then the finance task: the request is Ordered.
        await OpenTask(approver, V2Request, "approval");
        await Decide(approver, "Approve");
        await OpenTask(approver, V2Request, "finance");
        await Shot(approver, "06a-finance-task");
        await Decide(approver, "Approve");
        await OpenRequest(approver, V2Request);
        await Expect(WorkflowLink(approver, "Ordered")).ToBeVisibleAsync();
        await Expect(approver.GetByRole(AriaRole.Checkbox, new() { Name = "Ordered", Exact = true })).ToBeCheckedAsync();
        await Shot(approver, "06b-v2-request-ordered");

        // 7. The request started on v1 still follows v1: Approve goes straight on to Ordered, no finance task.
        await OpenTask(approver, V1Request, "approval");
        await Decide(approver, "Approve");
        await OpenRequest(approver, V1Request);
        await Expect(WorkflowLink(approver, "Ordered")).ToBeVisibleAsync();
        await Expect(approver.GetByRole(AriaRole.Checkbox, new() { Name = "Ordered", Exact = true })).ToBeCheckedAsync();
        await Shot(approver, "07-v1-request-ordered-without-finance");
        // 7b. The request's workflow, drawn from the version it runs on, with its current state highlighted (XSM-028).
        await WorkflowLink(approver, "Ordered").ClickAsync();
        await Expect(approver.Locator(".xafstateless-diagram-root:visible svg g.current")).ToContainTextAsync("Ordered");
        await Expect(approver.Locator(".xafstateless-diagram-root:visible svg")).Not.ToContainTextAsync("Finance");   // v1 has no finance
        await Expect(approver.Locator(".xafstateless-diagram-legend:visible")).ToContainTextAsync("Decision");
        await Shot(approver, "07b-v1-instance-diagram-current-state");
        await Nav(approver, "Workflow", "Workflow Task");
        await Expect(Row(approver, V1Request, "finance")).ToHaveCountAsync(0);   // the open-tasks list: nothing left for it
        await Expect(Row(approver, V2Request, "finance")).ToHaveCountAsync(0);

        // 8. The approver started both requests, so the seeded Notify @requester reached them (XSM-014).
        await Nav(approver, "Workflow", "Workflow Notification");
        await Expect(approver.GetByRole(AriaRole.Gridcell, new() { Name = $"Your request {V1Request} was ordered", Exact = true })).ToBeVisibleAsync();
        await Expect(approver.GetByRole(AriaRole.Gridcell, new() { Name = $"Your request {V2Request} was ordered", Exact = true })).ToBeVisibleAsync();
        await Shot(approver, "08-requester-notifications");
    }

    [Test, Order(2)]
    public async Task Designer_picks_the_target_type_of_a_new_definition_from_the_subjects()   // XSM-022
    {
        SetDefaultExpectTimeout(S.TimeoutMs);
        var designer = await LogIn(Page, S.Designer);
        await Nav(designer, "Workflow", "Workflow Definition");
        await Button(designer, "New").ClickAsync();
        var name = $"e2e-{Guid.NewGuid():N}"[..16];
        await designer.GetByRole(AriaRole.Textbox, new() { Name = "Name*" }).FillAsync(name);
        await Pick(designer, "Target Type Name*", "Purchase Request");   // the class caption; the full name is stored (XSM-025)
        await Button(designer, "Save").ClickAsync();
        await Expect(Button(designer, "Save")).ToBeDisabledAsync();
        await Refresh(designer);
        await Expect(designer.GetByRole(AriaRole.Combobox, new() { Name = "Target Type Name*", Exact = true }))
            .ToHaveValueAsync("Purchase Request");
        await Expect(designer.GetByRole(AriaRole.Spinbutton, new() { Name = "Version" })).ToHaveValueAsync("1");   // XSM-018 too
        await Shot(designer, "09-new-definition-target-type");
        // The list shows the stored value: the full class name, not the caption the editor displays.
        await Nav(designer, "Workflow", "Workflow Definition");
        await Expect(Row(designer, name, "XafStateless.Demo.Module.BusinessObjects.PurchaseRequest")).ToHaveCountAsync(1);
    }

    [Test, Order(3)]   // last: XAF restores a user's open tabs at the next login, and this test leaves the approver several
    public async Task Approver_starts_requests_from_the_entry_screen()   // XSM-033
    {
        SetDefaultExpectTimeout(S.TimeoutMs);
        var approver = await LogIn(Page, S.Approver);
        await CloseAllTabs(approver);   // XAF restores the user's tabs and caps them at 10 (MaxTabLimit): start clean
        var tag = Guid.NewGuid().ToString("N")[..6];
        string a = $"Entry A {tag}", b = $"Entry B {tag}", c = $"Entry C {tag}";
        await NewRequest(approver, a, 50);
        await NewRequest(approver, b, 2000);
        await NewRequest(approver, c, 70);

        // Opened by a plain navigation click (no Refresh): what the activator does on its own is what is tested.
        await NavPlain(approver, "Default", "Start requests");
        await Expect(Button(approver, "Start")).ToBeVisibleAsync();
        await Expect(Button(approver, "Save")).ToBeHiddenAsync();   // nothing to save on a screen
        await Button(approver, "Start").ClickAsync();
        await Expect(Alert(approver, "Pick a workflow.")).ToBeVisibleAsync();
        await Shot(approver, "10-start-requests-screen");

        var onlyLarge = approver.GetByRole(AriaRole.Checkbox, new() { Name = "Only large amounts" });
        await onlyLarge.CheckAsync();
        await Expect(ScreenRow(approver, b)).ToBeVisibleAsync();
        await Expect(ScreenRow(approver, a)).ToHaveCountAsync(0);
        await onlyLarge.UncheckAsync();
        await Expect(ScreenRow(approver, a)).ToBeVisibleAsync();
        await Expect(ScreenRow(approver, c)).ToBeVisibleAsync();

        await Pick(approver, "Workflow", "purchase-request");
        await Button(approver, "Start").ClickAsync();
        await Expect(Alert(approver, "Select at least one request.")).ToBeVisibleAsync();
        await SelectRow(approver, a);
        await SelectRow(approver, c);
        await Button(approver, "Start").ClickAsync();
        await Expect(Alert(approver, "Started 2 of 2.")).ToBeVisibleAsync();
        await Expect(ScreenRow(approver, a)).ToHaveCountAsync(0);   // started: no longer without a workflow
        await Expect(ScreenRow(approver, c)).ToHaveCountAsync(0);
        await Expect(ScreenRow(approver, b)).ToBeVisibleAsync();
        await Shot(approver, "10b-started");

        // One selected with "open after start": the request's own view replaces the screen, its workflow in Draft.
        await SelectRow(approver, b);
        await Button(approver, "Start").ClickAsync();
        await Expect(SelectedViewTab(approver)).ToContainTextAsync(b);
        try { await Expect(WorkflowLink(approver, "Draft")).ToBeVisibleAsync(); }
        catch (PlaywrightException) { await Shot(approver, "zz-entry-open-after-start-failed"); throw; }
        await Shot(approver, "10c-opened-after-start");

        // Back to the screen from the navigation: a fresh one, and leaving it asked nothing.
        await NavPlain(approver, "Default", "Start requests");
        await Expect(approver.GetByRole(AriaRole.Combobox, new() { Name = "Workflow", Exact = true })).ToHaveValueAsync("");
        await Expect(approver.GetByRole(AriaRole.Checkbox, new() { Name = "Open the request after starting" })).ToBeCheckedAsync();
        await Expect(ScreenRow(approver, b)).ToHaveCountAsync(0);
    }

    /// <summary>A row of the screen's nested list holding <paramref name="title"/>.</summary>
    ILocator ScreenRow(IPage p, string title) => p.GetByRole(AriaRole.Row).Filter(new() { Has = p.GetByRole(AriaRole.Gridcell, new() { Name = title, Exact = true }) });

    /// <summary>Select a nested-list row: its selection checkbox when the grid shows one, else a click on a non-link cell.</summary>
    async Task SelectRow(IPage p, string title)
    {
        var row = ScreenRow(p, title);
        var box = row.GetByRole(AriaRole.Checkbox);
        if (await box.CountAsync() > 0) await box.First.CheckAsync();
        else await row.GetByRole(AriaRole.Gridcell).Last.ClickAsync();
    }

    /// <summary>Close every closable tab (XAF restores a user's tabs at login; a session at the tab cap cannot open a view).</summary>
    async Task CloseAllTabs(IPage p)
    {
        var close = p.GetByRole(AriaRole.Main).Locator(".xaf-close-tab-button:visible");
        for (var n = await close.CountAsync(); n > 0; n = await close.CountAsync())
        {
            await close.First.ClickAsync();
            await Expect(close).ToHaveCountAsync(n - 1);
        }
    }

    /// <summary>A navigation click without the Refresh <see cref="Nav"/> adds: for a screen, initialization is the point.</summary>
    async Task NavPlain(IPage p, string group, string item)
    {
        var entry = p.GetByRole(AriaRole.Treeitem, new() { Name = item, Exact = true });
        if (!await entry.IsVisibleAsync()) await p.GetByRole(AriaRole.Application, new() { Name = group, Exact = true }).ClickAsync();
        await entry.ClickAsync();
        await Expect(SelectedViewTab(p)).ToContainTextAsync(item);
    }

    // ---- helpers: one per UI gesture ----

    /// <summary>Retries <paramref name="condition"/> until it holds or the expect timeout passes (the diagram redraws in JS).</summary>
    static async Task Until(string what, Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(S.TimeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting until {what}");
            await Task.Delay(200);
        }
    }

    async Task<IPage> LogIn(IPage page, string user)
    {
        page.SetDefaultTimeout(S.TimeoutMs);
        await page.GotoAsync(S.BaseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByLabel("User Name").FillAsync(user);
        if (S.Password.Length > 0) await page.GetByLabel("Password").FillAsync(S.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log In" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = user, Exact = true }).WaitForAsync();   // the user menu: logged in, circuit up
        return page;
    }

    ILocator Button(IPage p, string name) => p.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    /// <summary>A grid row holding both cell texts (only visible grids count, so hidden tabs never match).</summary>
    ILocator Row(IPage p, string cell1, string cell2) => p.GetByRole(AriaRole.Row)
        .Filter(new() { Has = p.GetByRole(AriaRole.Gridcell, new() { Name = cell1, Exact = true }) })
        .Filter(new() { Has = p.GetByRole(AriaRole.Gridcell, new() { Name = cell2, Exact = true }) });

    ILocator Alert(IPage p, string text) => p.GetByRole(AriaRole.Alert).Filter(new() { HasTextString = text }).Last;

    ILocator WorkflowLink(IPage p, string caption) => p.GetByRole(AriaRole.Main).GetByRole(AriaRole.Link, new() { Name = caption, Exact = true });

    ILocator SelectedViewTab(IPage p) => p.GetByRole(AriaRole.Main).GetByRole(AriaRole.Tab, new() { Selected = true })
        .Filter(new() { Has = p.Locator(".xaf-close-tab-button:visible") });

    async Task Refresh(IPage p) => await p.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Refresh") }).ClickAsync();

    /// <summary>Open a navigation item (expanding its group first) and refresh it: a reused tab can hold stale rows.</summary>
    async Task Nav(IPage p, string group, string item)
    {
        var entry = p.GetByRole(AriaRole.Treeitem, new() { Name = item, Exact = true });
        if (!await entry.IsVisibleAsync()) await p.GetByRole(AriaRole.Application, new() { Name = group, Exact = true }).ClickAsync();
        await entry.ClickAsync();
        await Expect(SelectedViewTab(p)).ToContainTextAsync(item);
        await Refresh(p);
    }

    /// <summary>Pick a value in a DevExpress combo; its own dropdown is found through aria-controls (others may linger).</summary>
    async Task Pick(IPage p, string combo, string option)
    {
        var box = p.GetByRole(AriaRole.Combobox, new() { Name = combo, Exact = true });
        // An ImmediatePostData combo re-renders after a pick, so a second pick can grab a dropdown that is being replaced:
        // on a timeout, close it and retry once with a fresh one.
        for (var attempt = 1; ; attempt++)
        {
            var timeout = attempt == 1 ? 5000 : S.TimeoutMs;
            await box.ClickAsync();
            await box.PressAsync("Alt+ArrowDown");
            try
            {
                // DevExpress 26.1.6 sets aria-controls only once the dropdown has opened (26.1.4 always had it): wait for the
                // open dropdown, then read it.
                await Expect(box).ToHaveAttributeAsync("aria-expanded", "true", new() { Timeout = timeout });
                await Expect(box).ToHaveAttributeAsync("aria-controls", new Regex(".+"), new() { Timeout = timeout });
                var list = await box.GetAttributeAsync("aria-controls");
                await p.Locator($"[id='{list}']").GetByRole(AriaRole.Option, new() { Name = option, Exact = true })
                    .ClickAsync(new() { Timeout = timeout });
                await Expect(box).ToHaveValueAsync(option, new() { Timeout = timeout });
                break;
            }
            // A click times out with System.TimeoutException, a Playwright assertion with PlaywrightException.
            catch (Exception e) when (attempt == 1 && e is TimeoutException or PlaywrightException)
            {
                await p.Keyboard.PressAsync("Escape");
                await Expect(box).ToHaveAttributeAsync("aria-expanded", "false");
            }
        }
    }

    /// <summary>Close a tab that has nothing left to save; focus returns to the owning definition.</summary>
    async Task CloseSavedTab(IPage p)
    {
        await Expect(Button(p, "Save")).ToBeDisabledAsync();
        await SelectedViewTab(p).Locator(".xaf-close-tab-button:visible").ClickAsync();
        await Expect(SelectedViewTab(p)).ToContainTextAsync("purchase-request");
    }

    /// <summary>Save the nested object's tab, then close it; focus returns to its owner (by default the definition).</summary>
    async Task SaveAndCloseTab(IPage p, string owner = "purchase-request")
    {
        await Button(p, "Save").ClickAsync();
        await Expect(Button(p, "Save")).ToBeDisabledAsync();   // saved: nothing pending
        await SelectedViewTab(p).Locator(".xaf-close-tab-button:visible").ClickAsync();
        await Expect(SelectedViewTab(p)).ToContainTextAsync(owner);
    }

    /// <summary>Run a choice action: one item runs on click, several drop down.</summary>
    /// <summary>Run a choice action. Decide and Transition always open their menu (ShowItemsOnClick): wait for the item,
    /// never guess. Start workflow runs its only item on click, or drops down when there are several.</summary>
    async Task Choose(IPage p, string action, string item)
    {
        await Button(p, action).ClickAsync();
        var choice = p.Locator(".dxbl-dropdown:visible, .dxbl-popup:visible, [role=menu]:visible").GetByText(item, new() { Exact = true }).Last;
        if (action is "Decide" or "Transition")
        {
            // XSM-029: clicking a menu item within milliseconds of the menu appearing sometimes executed its neighbour
            // (Approve clicked, Reject executed; about 1 run in 8). A 300 ms pause avoided the observed misrouting in
            // repeated runs (32/32 with a pause); the underlying cause remains unconfirmed.
            try
            {
                await choice.WaitForAsync();
                await p.WaitForTimeoutAsync(300);
                await choice.ClickAsync();
            }
            catch (TimeoutException) { await Shot(p, $"zz-choose-{action}-{item}-failed"); throw; }
            return;
        }
        try { await choice.ClickAsync(new() { Timeout = 3000 }); }
        catch (TimeoutException) { }   // single item: the click already ran it
    }

    async Task Decide(IPage p, string outcome)
    {
        await Choose(p, "Decide", outcome);
        await Expect(Button(p, "Decide")).ToBeHiddenAsync();   // decided: no outcomes left for this task
    }

    async Task NewRequest(IPage p, string title, decimal amount)
    {
        await Nav(p, "Default", "Purchase Request");
        await Button(p, "New").ClickAsync();
        await p.GetByRole(AriaRole.Textbox, new() { Name = "Title" }).FillAsync(title);
        await p.GetByRole(AriaRole.Spinbutton, new() { Name = "Amount" }).FillAsync(amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Button(p, "Save").ClickAsync();
        await Expect(Button(p, "Start workflow")).ToBeVisibleAsync();
    }

    async Task OpenRequest(IPage p, string title)
    {
        await Nav(p, "Default", "Purchase Request");
        await p.GetByRole(AriaRole.Gridcell, new() { Name = title, Exact = true }).ClickAsync();
        await Expect(SelectedViewTab(p)).ToContainTextAsync(title);
        await Refresh(p);   // the tab may be the one opened earlier
    }

    async Task OpenTask(IPage p, string subject, string stateKey)
    {
        await Nav(p, "Workflow", "Workflow Task");
        await Row(p, subject, stateKey).GetByRole(AriaRole.Gridcell, new() { Name = subject, Exact = true }).ClickAsync();
        try { await Expect(Button(p, "Decide")).ToBeVisibleAsync(); }
        catch (PlaywrightException) { await Shot(p, $"zz-open-task-{stateKey}-failed"); throw; }
    }

    string ShotPath(string name)
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, S.ScreenshotDir));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name + ".png");
    }

    async Task Shot(IPage p, string name)
    {
        var path = ShotPath(name);
        await p.ScreenshotAsync(new() { Path = path, Animations = ScreenshotAnimations.Disabled });
        TestContext.AddTestAttachment(path);
    }
}
