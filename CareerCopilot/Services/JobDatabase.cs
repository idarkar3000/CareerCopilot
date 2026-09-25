using Microsoft.Data.Sqlite;

namespace CareerCopilot.Services;

public class JobDatabase
{
    private const string ConnectionString = "Data Source=jobs.db";

    public JobDatabase()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS ProcessedJobs (
                Id TEXT PRIMARY KEY,
                Title TEXT,
                Company TEXT,
                Score INTEGER,
                ProcessedAt TEXT
            );
            CREATE TABLE IF NOT EXISTS SearchQueries (
                Query TEXT PRIMARY KEY
            );";
        command.ExecuteNonQuery();
    }

    public bool HasBeenProcessed(string jobId)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM ProcessedJobs WHERE Id = $id";
        command.Parameters.AddWithValue("$id", jobId);
        var result = (long)(command.ExecuteScalar() ?? 0L);
        return result > 0;
    }

    public void MarkAsProcessed(string jobId, string title, string company, int score)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO ProcessedJobs (Id, Title, Company, Score, ProcessedAt)
            VALUES ($id, $title, $company, $score, $processedAt);";
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$company", company);
        command.Parameters.AddWithValue("$score", score);
        command.Parameters.AddWithValue("$processedAt", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public List<string> GetSearchQueries()
    {
        var list = new List<string>();
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Query FROM SearchQueries";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    public bool AddSearchQuery(string query)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO SearchQueries (Query) VALUES ($q)";
        command.Parameters.AddWithValue("$q", query.Trim().ToLowerInvariant());
        return command.ExecuteNonQuery() > 0;
    }

    public bool RemoveSearchQuery(string query)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SearchQueries WHERE Query = $q";
        command.Parameters.AddWithValue("$q", query.Trim().ToLowerInvariant());
        return command.ExecuteNonQuery() > 0;
    }

    public void SeedDefaultQueries(List<string> defaults)
    {
        if (GetSearchQueries().Count > 0) return;
        foreach (var q in defaults)
        {
            AddSearchQuery(q);
        }
    }
}