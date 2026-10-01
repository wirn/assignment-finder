namespace AssignmentFinder.Brainville;

public interface IAssignmentSource
{
    string Name { get; }
    IAsyncEnumerable<Assignment> FetchAsync(CancellationToken cancellationToken = default);
}
