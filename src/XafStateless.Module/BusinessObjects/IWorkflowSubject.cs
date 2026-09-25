namespace XafStateless.Module.BusinessObjects;

public interface IWorkflowSubject
{
    Guid ID { get; }
    WorkflowInstance? Workflow { get; set; }
}
