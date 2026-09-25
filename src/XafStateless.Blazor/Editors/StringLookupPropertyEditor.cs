using DevExpress.Blazor;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Blazor.Components.Models;
using DevExpress.ExpressApp.Blazor.Editors;
using DevExpress.ExpressApp.Editors;
using DevExpress.ExpressApp.Model;
using XafStateless.Module.Services;

namespace XafStateless.Blazor.Editors;

/// <summary>A string picked from <see cref="IStringLookupSource.LookupValues"/> of the current object (docs 404597 shape).
/// The list is rebuilt in <see cref="ReadValueCore"/>, which runs whenever the editor re-reads the object, including after an
/// ImmediatePostData sibling changed. A stored value missing from the list is kept in it, so it still shows.</summary>
[PropertyEditor(typeof(string), StringLookup.Single, false)]
public class StringLookupPropertyEditor(Type objectType, IModelMemberViewItem model) : BlazorPropertyEditorBase(objectType, model), IComplexViewItem
{
    Func<IReadOnlyList<string>> roleNames = () => [];

    public void Setup(IObjectSpace objectSpace, XafApplication application) => roleNames = StringLookup.RoleNames(application.ServiceProvider);

    public override DxComboBoxModel<string, string> ComponentModel => (DxComboBoxModel<string, string>)base.ComponentModel;

    protected override IComponentModel CreateComponentModel() => new DxComboBoxModel<string, string>
    {
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
        var values = (CurrentObject as IStringLookupSource)?.LookupValues(PropertyName, roleNames).ToList() ?? [];
        if (PropertyValue is string current && current.Length > 0 && !values.Contains(current)) values.Add(current);
        ComponentModel.Data = values;
        base.ReadValueCore();
    }

    /// <summary>Read-only looks disabled, as the built-in combo adapter does it.</summary>
    protected override void ApplyReadOnly()
    {
        base.ApplyReadOnly();
        if (ComponentModel != null) ComponentModel.Enabled = AllowEdit;
    }
}
