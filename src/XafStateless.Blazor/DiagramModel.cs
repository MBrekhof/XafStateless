using DevExpress.ExpressApp.Blazor.Components.Models;

namespace XafStateless.Blazor;

/// <summary>Component model of <see cref="Diagram"/>; property names match its [Parameter]s (XAF maps them by name).</summary>
public class DiagramModel : ComponentModelBase
{
    public string MermaidText { get => GetPropertyValue<string>(); set => SetPropertyValue(value); }
    public string DetailedMermaidText { get => GetPropertyValue<string>(); set => SetPropertyValue(value); }
    public IReadOnlyList<string> Messages { get => GetPropertyValue<IReadOnlyList<string>>(); set => SetPropertyValue(value); }
    public override Type ComponentType => typeof(Diagram);
}
