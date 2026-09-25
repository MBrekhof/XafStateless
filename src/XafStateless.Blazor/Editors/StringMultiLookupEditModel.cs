using DevExpress.ExpressApp.Blazor.Components.Models;
using Microsoft.AspNetCore.Components;

namespace XafStateless.Blazor.Editors;

/// <summary>Component model of <see cref="StringMultiLookupEdit"/>; property names match its [Parameter]s (XAF maps them by name).</summary>
public class StringMultiLookupEditModel : ComponentModelBase
{
    public string? Value { get => GetPropertyValue<string?>(); set => SetPropertyValue(value); }
    public EventCallback<string> ValueChanged { get => GetPropertyValue<EventCallback<string>>(); set => SetPropertyValue(value); }
    public IReadOnlyList<string> LookupItems { get => GetPropertyValue<IReadOnlyList<string>>(); set => SetPropertyValue(value); }
    public bool ReadOnly { get => GetPropertyValue<bool>(); set => SetPropertyValue(value); }
    public override Type ComponentType => typeof(StringMultiLookupEdit);
}
