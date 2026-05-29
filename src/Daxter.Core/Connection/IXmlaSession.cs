using Daxter.Core.Query;

namespace Daxter.Core.Connection;

/// <summary>An open connection to an XMLA endpoint that can execute queries.</summary>
public interface IXmlaSession : IDisposable
{
    /// <summary>Executes a DAX, MDX, or DMV query and materializes the result.</summary>
    QueryResult Execute(string query);
}
