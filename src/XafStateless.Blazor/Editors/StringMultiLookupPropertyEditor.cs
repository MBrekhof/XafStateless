using System.Linq.Expressions;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Blazor.Components.Models;
using DevExpress.ExpressApp.Blazor.Editors;
using DevExpress.ExpressApp.Editors;
using DevExpress.ExpressApp.Model;
using Microsoft.AspNetCore.Components;
using XafStateless.Module.Services;

namespace XafStateless.Blazor.Editors;

/// <summary>Several strings picked from <see cref="IStringLookupSource.LookupValues"/>, stored joined with
/// <see cref="StringLookup.Separator"/>. A tag box in detail views, plain read-only text in list cells.</summary>
[PropertyEditor(typeof(string), StringLookup.Multi, false)]
public class StringMultiLookupPropertyEditor(Type objectType, IModelMemberViewItem model) : BlazorPropertyEditorBase(objectType, model), IComplexViewItem
{
    Func<IReadOnlyList<string>> roleNames = () => [], actionNames = () => [];

    public void Setup(IObjectSpace objectSpace, XafApplication application) =>
        (roleNames, actionNames) = (StringLookup.RoleNames(application.ServiceProvider), StringLookup.ActionNames(application.ServiceProvider));

    public override StringMultiLookupEditModel ComponentModel => (StringMultiLookupEditModel)base.ComponentModel;

    protected override IComponentModel CreateComponentModel()
    {
        var model = new StringMultiLookupEditModel { LookupItems = [] };
        model.ValueChanged = EventCallback.Factory.Create<string>(this, value =>
        {
            model.Value = value;
            OnControlValueChanged();
            WriteValue();
        });
        return model;
    }

    protected override void ReadValueCore()
    {
        base.ReadValueCore();
        ComponentModel.Value = (string?)PropertyValue;
        ComponentModel.LookupItems = (CurrentObject as IStringLookupSource)?.LookupValues(PropertyName, roleNames, actionNames).ToList() ?? [];
    }

    protected override object? GetControlValueCore() => ComponentModel.Value;

    protected override void ApplyReadOnly()
    {
        base.ApplyReadOnly();
        if (ComponentModel != null) ComponentModel.ReadOnly = !AllowEdit;
    }

    /// <summary>List cells: plain read-only text (the docs 402189 InputText model).</summary>
    protected override RenderFragment CreateViewComponentCore(object dataContext)
    {
        var model = new InputTextModel { Value = (string?)this.GetPropertyValue(dataContext) };
        model.ValueExpression = () => model.Value;
        model.SetAttribute("readonly", true);
        return model.GetComponentContent();
    }

    /// <summary>The standard Blazor InputText as a component model (docs 402189; a user-defined class there).</summary>
    public class InputTextModel : ComponentModelBase
    {
        public string? Value { get => GetPropertyValue<string?>(); set => SetPropertyValue(value); }
        public EventCallback<string> ValueChanged { get => GetPropertyValue<EventCallback<string>>(); set => SetPropertyValue(value); }
        public Expression<Func<string?>> ValueExpression { get => GetPropertyValue<Expression<Func<string?>>>(); set => SetPropertyValue(value); }
        public override Type ComponentType => typeof(Microsoft.AspNetCore.Components.Forms.InputText);
    }
}
