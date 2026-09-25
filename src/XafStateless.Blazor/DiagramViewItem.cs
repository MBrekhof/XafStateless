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

/// <summary>Mermaid diagram of the current WorkflowDefinition (draft or published), with validator messages under it.
/// The diagram is derived data, not a bound property, so nothing refreshes it automatically: it recomputes once when
/// its control is created and again whenever <see cref="IObjectSpace.Committed"/> fires (Save or Publish). It reads
/// <see cref="ViewItem.View"/>'s CurrentObject rather than its own (populated later, only via the View's propagation
/// loop), so the first render already has the right object.</summary>
[ViewItem(typeof(IModelDiagramViewItem))]
public sealed class DiagramViewItem(IModelViewItem model, Type objectType) :
    ViewItem(objectType, model.Id), IComponentContentHolder, IComplexViewItem
{
    DiagramModel? componentModel;
    IObjectSpace? objectSpace;
    DefinitionService? definitionService;
    Func<IReadOnlyList<string>> roleNames = () => [];

    DiagramModel ComponentModel => componentModel ??= new DiagramModel();

    RenderFragment IComponentContentHolder.ComponentContent =>
        ComponentModelObserver.Create(ComponentModel, ComponentModel.GetComponentContent());

    void IComplexViewItem.Setup(IObjectSpace objectSpace, XafApplication application)
    {
        this.objectSpace = objectSpace;
        definitionService = application.ServiceProvider.GetRequiredService<DefinitionService>();
        roleNames = StringLookup.RoleNames(application.ServiceProvider);
        objectSpace.Committed += ObjectSpace_Committed;
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

    void Update()
    {
        if (View?.CurrentObject is not WorkflowDefinition d)
        {
            ComponentModel.MermaidText = "";
            ComponentModel.Messages = [];
            return;
        }
        ComponentModel.MermaidText = WorkflowMermaid.Format(DefinitionMapper.ToDef(d));
        ComponentModel.Messages = definitionService!.Validate(objectSpace!, d, roleNames()).Messages;
    }

    protected override void Dispose(bool disposing)
    {
        if (objectSpace != null) objectSpace.Committed -= ObjectSpace_Committed;
        base.Dispose(disposing);
    }
}
