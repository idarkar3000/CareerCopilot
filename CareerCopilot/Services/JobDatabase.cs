using System.Globalization;
using CareerCopilot.Models;
using Microsoft.Data.Sqlite;

namespace CareerCopilot.Services;

/// <summary>
/// Ofertas ya vistas. Guarda también los datos de cada una para poder rehacer el CV (/cv) o
/// quitar el marcado (/unmark) sin scrapear otra vez.
/// </summary>
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

        // Columnas que se añadieron después de crear la tabla
        EnsureColumn(connection, "ProcessedJobs", "Link", "TEXT");
        EnsureColumn(connection, "ProcessedJobs", "Description", "TEXT");
        EnsureColumn(connection, "ProcessedJobs", "Province", "TEXT");
        EnsureColumn(connection, "ProcessedJobs", "City", "TEXT");
        EnsureColumn(connection, "ProcessedJobs", "IsRemote", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string type)
    {
        using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table});";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        reader.Close();

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        alter.ExecuteNonQuery();
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

    public void MarkAsProcessed(JobOffer offer, int score)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO ProcessedJobs
                (Id, Title, Company, Score, ProcessedAt, Link, Description, Province, City, IsRemote)
            VALUES ($id, $title, $company, $score, $processedAt, $link, $description, $province, $city, $remote);";
        command.Parameters.AddWithValue("$id", offer.Id);
        command.Parameters.AddWithValue("$title", offer.Title);
        command.Parameters.AddWithValue("$company", offer.Company);
        command.Parameters.AddWithValue("$score", score);
        command.Parameters.AddWithValue("$processedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$link", offer.Link);
        command.Parameters.AddWithValue("$description", offer.Description);
        command.Parameters.AddWithValue("$province", offer.Province);
        command.Parameters.AddWithValue("$city", offer.City);
        command.Parameters.AddWithValue("$remote", offer.IsRemote ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>Busca por id exacto y, si no hay, por fragmento de id, título o empresa.</summary>
    public JobOffer? FindProcessedOffer(string term, out int score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(term)) return null;

        var trimmed = term.Trim();
        var fragment = trimmed.Replace("%", string.Empty).Replace("_", string.Empty).Trim();
        var like = $"%{fragment}%";

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Title, Company, Link, Description, Province, City, IsRemote, Score
            FROM ProcessedJobs
            WHERE Id = $exact
               OR Id LIKE $likeId
               OR Title LIKE $likeTitle
               OR Link LIKE $likeLink
            ORDER BY (Id = $exact) DESC, ProcessedAt DESC
            LIMIT 1;";
        command.Parameters.AddWithValue("$exact", trimmed);
        command.Parameters.AddWithValue("$likeId", like);
        command.Parameters.AddWithValue("$likeTitle", like);
        command.Parameters.AddWithValue("$likeLink", like);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        score = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);

        return new JobOffer(
            reader.GetString(0),
            ReadString(reader, 1),
            ReadString(reader, 2),
            ReadString(reader, 3),
            ReadString(reader, 4),
            DateTime.UtcNow,
            ReadString(reader, 5),
            ReadString(reader, 6),
            !reader.IsDBNull(7) && reader.GetInt64(7) != 0);
    }

    /// <summary>Borra el registro para que la oferta vuelva a evaluarse.</summary>
    public bool RemoveProcessed(string id)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ProcessedJobs WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public (int Total, int Matches, double AverageScore, int Last24h) GetStats(int threshold)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(1),
                   COALESCE(SUM(CASE WHEN Score >= $threshold THEN 1 ELSE 0 END), 0),
                   COALESCE(AVG(Score), 0.0),
                   COALESCE(SUM(CASE WHEN ProcessedAt >= $since THEN 1 ELSE 0 END), 0)
            FROM ProcessedJobs;";
        command.Parameters.AddWithValue("$threshold", threshold);
        command.Parameters.AddWithValue("$since", DateTime.UtcNow.AddHours(-24).ToString("O", CultureInfo.InvariantCulture));

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (0, 0, 0, 0);

        return (
            reader.GetInt32(0),
            reader.GetInt32(1),
            Math.Round(reader.GetDouble(2), 1),
            reader.GetInt32(3));
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

    private static string ReadString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
}