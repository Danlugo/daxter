using Daxter.Core.Query;

namespace Daxter.Core.Formatting;

/// <summary>Renders a <see cref="QueryResult"/> to a string in a specific format.</summary>
public interface IResultFormatter
{
    string Format(QueryResult result);
}
