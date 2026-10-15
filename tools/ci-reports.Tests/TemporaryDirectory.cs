namespace CiReports.Tests;

/// <summary>A directory for one test, deleted afterwards.</summary>
public abstract class TemporaryDirectory : IDisposable
{
    protected string Root { get; } = Path.GetFullPath(Directory.CreateTempSubdirectory("ci-reports-").FullName);

    public void Dispose()
    {
        Directory.Delete(Root, true);
        GC.SuppressFinalize(this);
    }
}
