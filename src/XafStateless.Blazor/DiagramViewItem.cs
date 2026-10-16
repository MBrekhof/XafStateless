using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Blazor;
using DevExpress.ExpressApp.Blazor.Components;
using DevExpress.ExpressApp.Blazor.Components.Models;
using DevExpress.ExpressApp.Editors;
using DevExpress.ExpressApp.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Blazor;

public interface IModelDiagramViewItem : IModelViewItem;

/// <summary>Mermaid diagram, states colored by kind with a legend (XSM-028).
/// On a WorkflowDefinition: the definition as it is on screen, problem states in red and the validation messages under it;
/// the definition's controller validates on every change and pushes the result here (<see cref="IDefinitionValidationListener"/>,
/// XSM-027). On a WorkflowInstance: the instance's pinned definition with its current state highlighted.
/// It reads <see cref="ViewItem.View"/>'s CurrentObject rather than its own (populated later, only via the View's propagation
/// loop), so the first render already has the right object.</summary>
[ViewItem(typeof(IModelDiagramViewItem))]
public sealed class DiagramViewItem(IModelViewItem model, Type objectType) :
    ViewItem(objectType, model.Id), IComponentContentHolder, IComplexViewItem, IDefinitionValidationListener
{
    DiagramModel? componentModel;
    IObjectSpace? objectSpace;
    DefinitionResult? validation;   // the latest result pushed by the definition's controller

    DiagramModel ComponentModel => componentModel ??= new DiagramModel();

    RenderFragment IComponentContentHolder.ComponentContent =>
        ComponentModelObserver.Create(ComponentModel, ComponentModel.GetComponentContent());

    void IComplexViewItem.Setup(IObjectSpace objectSpace, XafApplication application)
    {
        this.objectSpace = objectSpace;
        objectSpace.Committed += ObjectSpace_Committed;
        objectSpace.Reloaded += ObjectSpace_Committed;
    }

    protected override object CreateControlCore()
    {
        Update();
        return ComponentModel;
    }

    protected override void OnCurrentObjectChanged()
    {
        base.OnCurrentObjectChanged();
        Update();
    }

    void ObjectSpace_Committed(object? sender, EventArgs e) => Update();

    public void OnValidated(DefinitionResult? result)
    {
        validation = result;
        Update();
    }

    void Update()
    {
        switch (View?.CurrentObject)
        {
            case WorkflowDefinition d:
                var problems = validation?.Findings?.Where(f => f.State != null).Select(f => f.State!).ToHashSet();
                SetDiagram(DefinitionMapper.ToDef(d, objectSpace), new DiagramStyle(ByKind: true, Problems: problems), validation?.Messages ?? []);
                break;
            case WorkflowInstance { Definition: { } pinned } i:
                SetDiagram(DefinitionMapper.ToDef(pinned), new DiagramStyle(ByKind: true, Current: i.StateKey), []);
                break;
            default:
                ComponentModel.MermaidText = "";
                ComponentModel.DetailedMermaidText = "";
                ComponentModel.Messages = [];
                break;
        }
    }

    void SetDiagram(WorkflowDef def, DiagramStyle style, IReadOnlyList<string> messages)
    {
        ComponentModel.MermaidText = WorkflowMermaid.Format(def, style);
        ComponentModel.DetailedMermaidText = WorkflowMermaid.Format(def, style with { Details = true });
        ComponentModel.Messages = messages;
    }

    protected override void Dispose(bool disposing)
    {
        if (objectSpace != null) { objectSpace.Committed -= ObjectSpace_Committed; objectSpace.Reloaded -= ObjectSpace_Committed; }
        base.Dispose(disposing);
    }
}
