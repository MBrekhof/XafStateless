using System.Globalization;
using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.EFCore;
using DevExpress.ExpressApp.Security;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

public sealed record RunResult(bool Ok, string Message, IReadOnlyList<Step> Steps);

/// <summary>Who is acting. Secured = the caller's secured object space (read + authorization only, never written by the runtime);
/// NewCommandSpace = a factory for non-secured object spaces (INonSecuredObjectSpaceFactory), in which the runtime does its writes;
/// Security = the strategy that secures <see cref="Secured"/>, for the Write check of Start and Fire. A secured space
/// (<see cref="ISecuredObjectSpace"/>) without it is refused; a plain space (no security configured) needs none.
/// UserId = the logged-on user's key; Start records it as the instance's requester (null: no requester).</summary>
public sealed record WorkflowCaller(string Name, Func<string, bool> InRole, IObjectSpace? Secured, Func<IObjectSpace> NewCommandSpace,
    IRequestSecurityStrategy? Security = null, Guid? UserId = null)
{
    public static WorkflowCaller System(Func<IObjectSpace> newCommandSpace) => new("system", _ => true, null, newCommandSpace);
}

/// <summary>
/// Singleton: holds no object space. Every operation is a command: per attempt (up to <see cref="MaxAttempts"/>, retried on a
/// lost race) a fresh command object space, a reload by id, link and authorization checks against the persisted rows, the
/// machine run inside a <see cref="RuntimeScope"/>, a Touch of every row the command stands on, and ONE CommitChanges.
/// Entry points split by who may cause them: Fire leaves Manual states, Decide leaves Task states, RunDueTimers leaves Wait
/// states, Decision states leave automatically.
/// </summary>
public sealed class WorkflowRuntime(DefinitionStore store, TimeProvider clock, WorkflowActionCatalog? actions = null)
{
    readonly WorkflowActionCatalog catalog = actions ?? WorkflowActionCatalog.Empty;

    public const int MaxAttempts = 4;
    public const string LinkMismatch = "workflow link mismatch";
    public const string AlreadyDecided = "already decided";
    public const string LostRaceMessage = "The workflow changed meanwhile (another action committed first). Reload and try again.";

    /// <summary>Test seam: ("after-load" | "before-commit", attempt number).</summary>
    internal Action<string, int>? TestHook { get; set; }

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    enum By { Subject, Task, Instance }

    /// <summary>One attempt's reloaded rows, all from the command object space.</summary>
    sealed class Cmd(IObjectSpace os, ITypeInfo type, IWorkflowSubject subject)
    {
        public IObjectSpace Os { get; } = os;
        public ITypeInfo Type { get; } = type;
        public IWorkflowSubject Subject { get; } = subject;
        public WorkflowInstance? Instance { get; set; }
        public WorkflowTask? Task { get; set; }
        public WorkflowDef? Def { get; set; }
        public IReadOnlyList<Step> Steps { get; set; } = [];
        /// <summary>This attempt's write permit; SetField records its (subject, member) exemption here.</summary>
        public RuntimeScope? Scope { get; set; }
        /// <summary>Things this attempt skipped, reported with the result (a requester Notify without a requester).</summary>
        public List<string> Notes { get; } = [];
    }

    public RunResult Start(WorkflowCaller c, Type subjectType, Guid subjectId, string definitionName) =>
        Run(c, By.Subject, subjectType, subjectId, write: true, x =>
        {
            if (x.Subject.Workflow != null) return "the workflow has already started";
            if (store.Published(x.Os, definitionName) is not { } pinned) return $"'{definitionName}' has no published version";
            if (pinned.TargetTypeName != subjectType.FullName) return $"definition '{definitionName}' is for {pinned.TargetTypeName}";
            var instance = x.Os.CreateObject<WorkflowInstance>();
            (instance.SubjectType, instance.SubjectId, instance.Definition) = (subjectType.FullName!, x.Subject.ID, pinned);
            (instance.StartedById, instance.StartedBy) = (c.UserId, c.UserId is null ? null : c.Name);   // before the initial state's effects
            x.Subject.Workflow = instance;
            (x.Instance, x.Def) = (instance, store.Get(x.Os, pinned.ID));
            x.Steps = Machine(x).EnterInitial();
            return null;
        }, x => $"Started '{definitionName}' v{x.Def!.Version}.");

    public IReadOnlyList<TransitionDef> Available(WorkflowCaller c, Type subjectType, Guid subjectId)
    {
        using var os = c.NewCommandSpace();
        var (x, refusal) = Load(os, c, By.Subject, subjectType, subjectId, write: true);
        if (refusal != null || x!.Def == null || x.Def.State(x.Instance!.StateKey).Kind != StateKind.Manual) return [];
        return Machine(x).Available().Where(t => t.RequiredRole is null || c.InRole(t.RequiredRole)).ToList();
    }

    public IReadOnlyList<string> Outcomes(WorkflowCaller c, Guid taskId)
    {
        using var os = c.NewCommandSpace();
        var (x, refusal) = Load(os, c, By.Task, null, taskId, write: false);
        if (refusal != null || x!.Task is not { State: TaskState.Open } task || task.StateKey != x.Instance!.StateKey || !c.InRole(task.Role)) return [];
        return OutcomesOf(x.Def!, task.StateKey);
    }

    static List<string> OutcomesOf(WorkflowDef def, string stateKey) => def.From(stateKey).Select(t => t.Trigger).Distinct().ToList();

    public RunResult Fire(WorkflowCaller c, Type subjectType, Guid subjectId, string trigger) =>
        Run(c, By.Subject, subjectType, subjectId, write: true, x =>
        {
            if (x.Instance == null) return "the workflow has not started";
            var state = x.Def!.State(x.Instance.StateKey);
            switch (state.Kind)
            {
                case StateKind.Manual: break;
                case StateKind.Task: return $"'{trigger}' can only be decided through its task";
                case StateKind.Wait: return $"'{state.Key}' leaves by its timer";
                default: return NotPossible(trigger, state.Key);
            }
            var permitted = Machine(x).Available().Where(t => t.Trigger == trigger).ToList();
            if (permitted.Count == 0) return NotPossible(trigger, state.Key);
            if (permitted.FirstOrDefault(t => t.RequiredRole is { } role && !c.InRole(role)) is { } denied)
                return $"'{trigger}' requires the role '{denied.RequiredRole}'";
            x.Steps = Machine(x).Fire(trigger);
            return null;
        }, x => $"'{trigger}' done.");

    public RunResult Decide(WorkflowCaller c, Guid taskId, string outcome) =>
        Run(c, By.Task, null, taskId, write: false, x =>
        {
            var task = x.Task!;
            if (task.State != TaskState.Open) return AlreadyDecided;
            if (task.StateKey != x.Instance!.StateKey) return LinkMismatch;   // an Open task always belongs to the current state
            if (!c.InRole(task.Role)) return $"deciding this task requires the role '{task.Role}'";
            if (!OutcomesOf(x.Def!, task.StateKey).Contains(outcome)) return $"'{outcome}' is not an outcome of this task";
            if (!Machine(x).Available().Any(t => t.Trigger == outcome)) return NotPossible(outcome, task.StateKey);
            // Decided before firing, so Enter's supersede leaves this task alone.
            (task.State, task.DecidedBy, task.Outcome, task.DecidedUtc) = (TaskState.Decided, c.Name, outcome, Now);
            x.Steps = Machine(x).Fire(outcome);
            return null;
        }, _ => $"'{outcome}' decided.");

    /// <summary>Fires every due Wait instance (id order), each as its own command with caller "system". An instance another
    /// writer already moved, or that lost the race on all attempts, is skipped (counted as not fired); any other failure is
    /// collected and rethrown as an AggregateException after the rest have run.</summary>
    public int RunDueTimers(Func<IObjectSpace> newCommandSpace)
    {
        var now = Now;
        List<Guid> due;
        using (var os = newCommandSpace())
            due = os.GetObjectsQuery<WorkflowInstance>().Where(i => i.DueUtc != null && i.DueUtc <= now).OrderBy(i => i.ID).Select(i => i.ID).ToList();
        var system = WorkflowCaller.System(newCommandSpace);
        var (fired, failures) = (0, new List<Exception>());
        foreach (var id in due)
        {
            try
            {
                var r = Run(system, By.Instance, null, id, write: false, x =>
                {
                    if (x.Def!.State(x.Instance!.StateKey).Kind != StateKind.Wait || x.Instance.DueUtc is not { } at || at > now)
                        return NotDue;   // another writer moved it meanwhile
                    x.Steps = Machine(x).FireTimer();
                    return null;
                }, _ => "timer fired");
                if (r.Ok) fired++;
                else if (r.Message is not (NotDue or LostRaceMessage))   // only those two are skips; anything else is a failure
                    failures.Add(new InvalidOperationException($"Workflow timer of instance {id}: {r.Message}"));
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException("Workflow timers failed for some instances; nothing of theirs was committed.", failures);
        return fired;
    }

    const string NotDue = "not due";

    static string NotPossible(string trigger, string state) => $"'{trigger}' is not possible from '{state}'";

    /// <summary>The one command loop behind Start, Fire, Decide and each timer. <paramref name="apply"/> returns a refusal
    /// (nothing is committed) or null after running the machine. <paramref name="write"/>: the caller needs Write on the subject.</summary>
    RunResult Run(WorkflowCaller c, By by, Type? subjectType, Guid id, bool write, Func<Cmd, string?> apply, Func<Cmd, string> done)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var os = c.NewCommandSpace();
                var (x, refusal) = Load(os, c, by, subjectType, id, write);
                if (refusal != null) return new(false, refusal, []);
                TestHook?.Invoke("after-load", attempt);
                // Bound to this command's DbContext; disposed only after CommitChanges returned or threw.
                using var scope = RuntimeScope.Open((os as EFCoreObjectSpace)?.DbContext ?? throw new NotSupportedException("The workflow runtime needs EF Core command object spaces."));
                x!.Scope = scope;
                if (apply(x!) is { } refused) return new(false, refused, []);
                foreach (var row in new object?[] { x!.Subject, x.Instance, x.Task })
                    if (row != null && !os.IsNewObject(row)) Concurrency.Touch(os, row);
                TestHook?.Invoke("before-commit", attempt);
                os.CommitChanges();
                return new(true, string.Join(" ", [done(x), .. x.Notes.Distinct()]), x.Steps);
            }
            catch (Exception ex) when (LostRace.Is(ex))
            {
                if (attempt == MaxAttempts) return new(false, LostRaceMessage, []);
            }
        }
    }

    /// <summary>Reloads subject, instance and task by id and checks the links between them (any mismatch: <see cref="LinkMismatch"/>),
    /// and that the caller's secured object space can read the subject; with <paramref name="write"/>, that the caller may also
    /// write it (Start and Fire change the subject's workflow; Decide is authorized by the task's role instead).</summary>
    (Cmd? Cmd, string? Refusal) Load(IObjectSpace os, WorkflowCaller c, By by, Type? subjectType, Guid id, bool write)
    {
        WorkflowTask? task = null;
        WorkflowInstance? instance = null;
        var subjectId = id;
        if (by == By.Task)
        {
            if ((task = os.GetObjectByKey<WorkflowTask>(id)) == null) return (null, "the task does not exist");
            if ((instance = task.Instance) == null) return (null, LinkMismatch);
        }
        else if (by == By.Instance && (instance = os.GetObjectByKey<WorkflowInstance>(id)) == null)
            return (null, "the workflow instance does not exist");
        if (instance != null)
            (subjectType, subjectId) = (os.TypesInfo.FindTypeInfo(instance.SubjectType)?.Type, instance.SubjectId);

        var type = subjectType == null ? null : os.TypesInfo.FindTypeInfo(subjectType);
        if (type is not { IsPersistent: true } || !typeof(IWorkflowSubject).IsAssignableFrom(type.Type) || type.KeyMember?.MemberType != typeof(Guid))
            return (null, LinkMismatch);
        var secured = c.Secured?.GetObjectByKey(type.Type, subjectId);
        if (os.GetObjectByKey(type.Type, subjectId) is not IWorkflowSubject subject || (c.Secured != null && secured == null))
            return (null, $"the {type.Name} does not exist or cannot be read");
        if (write && c.Secured is ISecuredObjectSpace && c.Security?.CanWrite(c.Secured, secured!) != true)
            return (null, $"changing the workflow of this {type.Name} requires write access to it");
        instance ??= by == By.Subject ? subject.Workflow : null;
        if (instance != null && (instance.SubjectType != type.Type.FullName || instance.SubjectId != subject.ID
                                 || subject.Workflow != instance || instance.Definition == null))
            return (null, LinkMismatch);
        return (new Cmd(os, type, subject)
        {
            Instance = instance,
            Task = task,
            Def = instance == null ? null : store.Get(os, instance.Definition!.ID),
        }, null);
    }

    WorkflowMachine Machine(Cmd x) =>
        new(x.Def!, () => x.Instance!.StateKey, key => x.Instance!.StateKey = key, criteria => Fits(x, criteria), key => Enter(x, key));

    /// <summary>Criteria on the command space's subject: the object Enter modifies, so a SetField is visible to the next Decision.</summary>
    static bool Fits(Cmd x, string criteria) =>
        x.Os.GetExpressionEvaluator(x.Type.Type, CriteriaOperator.Parse(criteria)).Fit(x.Subject);

    /// <summary>onEnter: runs for every entered state, before the next Decision is evaluated.</summary>
    void Enter(Cmd x, string key)
    {
        var (instance, state, now) = (x.Instance!, x.Def!.State(key), Now);
        foreach (var open in x.Os.GetObjectsQuery<WorkflowTask>().Where(t => t.Instance!.ID == instance.ID && t.State == TaskState.Open).ToList())
            if (open.State == TaskState.Open) open.State = TaskState.Superseded;   // the query filters on the database value; the tracked one may be Decided
        (instance.StateKey, instance.StateCaption, instance.EnteredUtc, instance.DueUtc) = (key, state.Caption, now, null);

        foreach (var effect in state.Effects)
            switch (effect.Kind)
            {
                case EffectKind.SetField:
                    Write(x, WorkflowMembers.Supported(x.Type).FirstOrDefault(m => m.Name == effect.Target)
                             ?? throw new InvalidOperationException($"'{effect.Target}' cannot be set by a workflow"),
                        member => XafWorkflowEnvironment.ConvertValue(member.MemberType, effect.Value));
                    break;
                case EffectKind.Increment:
                    Write(x, WorkflowMembers.Counters(x.Type).FirstOrDefault(m => m.Name == effect.Target)
                             ?? throw new InvalidOperationException($"'{effect.Target}' cannot be incremented by a workflow"),
                        member =>
                        {
                            XafWorkflowEnvironment.TryStep(effect.Value, out var step);   // validated at publish
                            try   // the decimal sum itself can overflow, not only the conversion back
                            {
                                var sum = (member.GetValue(x.Subject) is { } current ? Convert.ToDecimal(current, CultureInfo.InvariantCulture) : 0) + step;
                                return Convert.ChangeType(sum, Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType, CultureInfo.InvariantCulture);
                            }
                            catch (OverflowException) { throw new UserFriendlyException($"'{member.Name}' cannot be incremented past its range"); }
                        });
                    break;
                case EffectKind.Notify:
                    var toRequester = effect.Target == EffectDef.Requester;
                    if (toRequester && instance.StartedById is null)
                    {
                        x.Notes.Add("A requester notification was skipped: the workflow was started without a user (no requester).");
                        break;
                    }
                    var note = x.Os.CreateObject<WorkflowNotification>();
                    (note.Instance, note.State, note.CreatedUtc) = (instance, NotificationState.Pending, now);
                    if (toRequester) (note.UserId, note.UserName) = (instance.StartedById, instance.StartedBy);
                    else note.Role = effect.Target;
                    note.Body = Fill(x, effect.Value);
                    break;
                case EffectKind.RunAction:
                    // Trusted host code inside this command (see IWorkflowAction): its writes through x.Os commit with the transition.
                    var action = catalog.Find(effect.Target)
                                 ?? throw new InvalidOperationException($"workflow action '{effect.Target}' is not registered in this application");
                    action.Run(new WorkflowActionContext(x.Os, x.Subject, Fill(x, effect.Value ?? "")));   // no argument is ""
                    break;
                default:
                    throw new InvalidOperationException($"unknown effect kind {effect.Kind}");
            }

        switch (state.Kind)
        {
            case StateKind.Task:
                var task = x.Os.CreateObject<WorkflowTask>();
                (task.Instance, task.StateKey, task.Role, task.State) = (instance, key, state.TaskRole ?? "", TaskState.Open);
                task.Subject = Convert.ToString(x.Type.DefaultMember?.GetValue(x.Subject), CultureInfo.InvariantCulture) ?? x.Subject.ToString() ?? "";
                break;
            case StateKind.Wait:
                instance.DueUtc = now.AddMinutes(state.WaitMinutes ?? 0);
                break;
        }
    }

    /// <summary>A Notify body or an action argument: its {Member} placeholders read from the subject. A null Notify body throws, as
    /// before (validation refuses it; only a definition that bypassed validation has one).</summary>
    static string Fill(Cmd x, string template) => XafWorkflowEnvironment.Placeholder().Replace(template, m =>
        Convert.ToString(x.Type.FindMember(m.Groups[1].Value)?.GetValue(x.Subject), CultureInfo.InvariantCulture) ?? "");

    /// <summary>A SetField or Increment write of one subject member, exempted from StateLockGuard for this member only; keyed by
    /// the EF entity type (never a proxy type), as the guard keys it.</summary>
    static void Write(Cmd x, IMemberInfo member, Func<IMemberInfo, object?> value)
    {
        var runtimeScope = x.Scope ?? throw new InvalidOperationException("effects run only inside the runtime scope");
        member.SetValue(x.Subject, value(member));
        runtimeScope.Allow(runtimeScope.Context.Entry(x.Subject).Metadata.ClrType, x.Subject.ID, member.Name);
    }
}
