using DevExpress.Blazor;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Blazor.Components.Models;
using DevExpress.ExpressApp.Blazor.Editors;
using DevExpress.ExpressApp.Editors;
using DevExpress.ExpressApp.Model;
using XafStateless.Module.Services;

namespace XafStateless.Blazor.Editors;

/// <summary>One list entry: the stored <see cref="Value"/> and the <see cref="Text"/> people see (XSM-025).</summary>
public sealed record LookupItem(string Value, string Text);

/// <summary>A string picked from <see cref="IStringLookupSource.LookupValues"/> of the current object (docs 404597 shape),
/// shown as <see cref="IStringLookupSource.LookupText"/>. The list is rebuilt in <see cref="ReadValueCore"/>, which runs
/// whenever the editor re-reads the object, including after an ImmediatePostData sibling changed. A stored value missing
/// from the list is kept in it, so it still shows.</summary>
[PropertyEditor(typeof(string), StringLookup.Single, false)]
public class StringLookupPropertyEditor(Type objectType, IModelMemberViewItem model) : BlazorPropertyEditorBase(objectType, model), IComplexViewItem
{
    Func<IReadOnlyList<string>> roleNames = () => [];

    public void Setup(IObjectSpace objectSpace, XafApplication application) => roleNames = StringLookup.RoleNames(application.ServiceProvider);

    public override DxComboBoxModel<LookupItem, string> ComponentModel => (DxComboBoxModel<LookupItem, string>)base.ComponentModel;

    protected override IComponentModel CreateComponentModel() => new DxComboBoxModel<LookupItem, string>
    {
        ValueFieldName = nameof(LookupItem.Value),
        TextFieldName = nameof(LookupItem.Text),
        AllowUserInput = false,
        // docs 404597 sets FilteringMode = Contains, obsolete in 26.1; these are its replacements, as StringPropertyEditor uses them.
        SearchMode = ListSearchMode.AutoSearch,
        SearchFilterCondition = ListSearchFilterCondition.Contains,
        ClearButtonDisplayMode = AllowNull ? DataEditorClearButtonDisplayMode.Auto : DataEditorClearButtonDisplayMode.Never,
        NullText = NullText,
        Data = [],
    };

    protected override void ReadValueCore()
    {
        var source = CurrentObject as IStringLookupSource;
        var values = source?.LookupValues(PropertyName, roleNames).ToList() ?? [];
        if (PropertyValue is string current && current.Length > 0 && !values.Contains(current)) values.Add(current);
        ComponentModel.Data = values.Select(v => new LookupItem(v, source?.LookupText(PropertyName, v) ?? v)).ToList();
        base.ReadValueCore();
    }

    /// <summary>Read-only looks disabled, as the built-in combo adapter does it.</summary>
    protected override void ApplyReadOnly()
    {
        base.ApplyReadOnly();
        if (ComponentModel != null) ComponentModel.Enabled = AllowEdit;
    }
}
