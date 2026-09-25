using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Model;
using DevExpress.Persistent.Base;

namespace XafStateless.Module.BusinessObjects;

/// <summary>The Dry run popup: a path of triggers from the initial state, the criteria to treat as true, and the outcome.</summary>
[DomainComponent]
public class DryRunParameters : NonPersistentBaseObject
{
    string path = "", trueCriteria = "", result = "";

    [ToolTip("Triggers to fire from the initial state, separated by ';'")]
    public string Path { get => path; set => SetPropertyValue(ref path, value); }

    [ToolTip("Criteria texts that count as true, separated by ';' (every other criteria is false)")]
    public string TrueCriteria { get => trueCriteria; set => SetPropertyValue(ref trueCriteria, value); }

    [ModelDefault("AllowEdit", "False"), ModelDefault("RowCount", "8")]
    public string Result { get => result; set => SetPropertyValue(ref result, value); }
}
