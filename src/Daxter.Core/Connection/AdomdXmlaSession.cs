using Daxter.Core.Query;
using Microsoft.AnalysisServices.AdomdClient;

namespace Daxter.Core.Connection;

/// <summary>An <see cref="IXmlaSession"/> backed by a live ADOMD.NET connection.</summary>
public sealed class AdomdXmlaSession : IXmlaSession
{
    private readonly AdomdConnection _connection;

    public AdomdXmlaSession(AdomdConnection connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public QueryResult Execute(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new DaxterException("Query text is empty.");
        }

        try
        {
            using var command = new AdomdCommand(query, _connection);
            using var reader = command.ExecuteReader();

            var columns = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns[i] = reader.GetName(i);
            }

            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    row[i] = value is DBNull ? null : value;
                }

                rows.Add(row);
            }

            return new QueryResult(columns, rows);
        }
        catch (AdomdException ex)
        {
            throw new DaxterException($"Query failed: {ex.Message}", ex);
        }
    }

    public void ExecuteCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new DaxterException("Command text is empty.");
        }

        try
        {
            using var cmd = new AdomdCommand(command, _connection);
            cmd.ExecuteNonQuery();
        }
        catch (AdomdException ex)
        {
            throw new DaxterException($"Command failed: {ex.Message}", ex);
        }
    }

    public void Dispose() => _connection.Dispose();
}
