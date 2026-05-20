#:package Microsoft.Data.Sqlite@10.0.8
#:package Spectre.Console@0.55.2

using Microsoft.Data.Sqlite;
using Spectre.Console;

using var database = new Database("Data Source=movies.db");

while (true)
{
    AnsiConsole.Markup("[grey]> [/]");
    var input = Console.ReadLine();

    if (input == null || !input.StartsWith('/'))
    {
        AnsiConsole.MarkupLine("[red]Invalid command. Commands must start with '/'[/]");
        continue;
    }

    CommandHandler handler = new CommandHandler(database);
    handler.HandleCommand(input.Trim());
}

sealed class CommandHandler
{
    private readonly Database _database;
    // Dictionary of commands and their corresponding actions, case-insensitive
    private readonly Dictionary<string, Action> _commands;

    public CommandHandler(Database database)
    {
        _database = database;
        _commands = new(StringComparer.OrdinalIgnoreCase)
        {
            ["/help"] = HelpCommand,
            ["/list"] = ListCommand,
            ["/exit"] = TerminateSession,
            ["/quit"] = TerminateSession,
            ["/q"] = TerminateSession,
        };
    }

    public void HandleCommand(string command)
    {
        if (!_commands.TryGetValue(command, out Action? action))
        {
            AnsiConsole.MarkupLine("[red]Unknown command[/]");
            return;
        }
        action();
    }

    private static void HelpCommand()
    {
        AnsiConsole.MarkupLine("""
            [bold green]Available commands:[/]
            [bold blue]/help[/] - Show this help message
            [bold blue]/list[/] - List all movies
            [bold blue]/exit[/] - Exit the application
        """);
    }

    private void ListCommand()
    {
        var movies = _database.GetMovies();
        AnsiConsole.MarkupLine("List");
        foreach (var movie in movies)
        {
            AnsiConsole.MarkupLine($"[bold green]{movie.Title}[/] ({movie.Year}) - {movie.Genre} - {movie.Rating}");
        }
    }

    private static void TerminateSession()
    {
        Environment.Exit(0);
    }
}


sealed class Database : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public Database(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        try
        {
            _connection.Open();
            EnablePRAGMAs();
            InitializeDatabase();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    private void EnablePRAGMAs()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = 2000;
            PRAGMA busy_timeout = 5000;
        """;
        command.ExecuteNonQuery();
    }

    private void InitializeDatabase()
    {
        using var transaction = _connection.BeginTransaction(deferred: false); // IMMEDIATE transaction
        using var command = _connection.CreateCommand();
        command.Transaction = transaction; // make sure the command is executed in the same transaction

        var userVersion = GetUserVersion(command);

        // On first run, create the database schema and seed the data
        if (userVersion == 0)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS ratings (
                    id INTEGER PRIMARY KEY,
                    rating TEXT NOT NULL UNIQUE
                );

                CREATE TABLE IF NOT EXISTS movies (
                    id INTEGER PRIMARY KEY,
                    title TEXT NOT NULL,
                    year INTEGER NOT NULL,
                    genre TEXT NOT NULL,
                    rating_id INTEGER NOT NULL,
                    imdb_url TEXT,
                    FOREIGN KEY (rating_id) REFERENCES ratings(id) ON DELETE RESTRICT
                );

                CREATE INDEX IF NOT EXISTS idx_movies_rating_id
                ON movies (rating_id);
                """;

            command.ExecuteNonQuery();

            command.CommandText = """
                INSERT OR IGNORE INTO ratings (rating)
                VALUES
                    ('Very Poor'),
                    ('Poor'),
                    ('Average'),
                    ('Good'),
                    ('Excellent');
                """;

            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static int GetUserVersion(SqliteCommand command)
    {
        command.CommandText = "PRAGMA user_version;";
        var result = command.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    public List<MovieListItem> GetMovies()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT
                m.title,
                m.year,
                m.genre,
                r.rating,
                m.imdb_url
            FROM movies AS m
            INNER JOIN ratings AS r ON r.id = m.rating_id
            ORDER BY m.title, m.year;
        """;

        using var reader = command.ExecuteReader();
        var movies = new List<MovieListItem>();

        while (reader.Read())
        {
            movies.Add(
                new MovieListItem(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)
                )
            );
        }

        return movies;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _connection.Dispose();
        _disposed = true;
    }
}

record Movie(string Title, int Year, string Genre, int RatingId, string? ImdbUrl = null);
record MovieListItem(string Title, int Year, string Genre, string Rating, string? ImdbUrl = null);