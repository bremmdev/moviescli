#:package Microsoft.Data.Sqlite@10.0.8
#:package Spectre.Console@0.55.2

using System.Text;
using Microsoft.Data.Sqlite;
using Spectre.Console;

using var database = new Database("Data Source=movies.db");
var handler = new CommandHandler(database);

while (true)
{
    AnsiConsole.Markup("[grey]> [/]");
    var input = Console.ReadLine();

    if (input == null || !input.Trim().StartsWith('/'))
    {
        AnsiConsole.MarkupLine("[red]Invalid command. Commands must start with '/'[/]");
        continue;
    }

    // If the command returns false, exit the main loop to cleanly exit the application
    // Needed so WAL journal is cleanly checked in
    if (!handler.HandleCommand(input.Trim()))
        break;
}

sealed class CommandHandler
{
    private readonly Database _database;
    // Dictionary of commands and their corresponding actions, case-insensitive
    private readonly Dictionary<string, Func<string[], bool>> _commands;

    public CommandHandler(Database database)
    {
        _database = database;
        _commands = new(StringComparer.OrdinalIgnoreCase)
        {
            ["/help"] = _ => { HelpCommand(); return true; },
            ["/list"] = _ => { ListCommand(); return true; },
            ["/add"] = (args) => { AddCommand(args); return true; },
            ["/exit"] = _ => false,
            ["/quit"] = _ => false,
            ["/q"] = _ => false,
        };
    }

    public bool HandleCommand(string input)
    {
        var parts = SplitCommandArgs(input.Trim());
        if (parts.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Unknown command[/]");
            return true;
        }

        var command = parts[0];
        var args = parts.Count > 1 ? parts.Skip(1).ToArray() : [];

        if (!_commands.TryGetValue(command, out Func<string[], bool>? action))
        {
            AnsiConsole.MarkupLine("[red]Unknown command[/]");
            return true;
        }

        return action(args);
    }

    // Split the command arguments into a list of strings
    // Handles quoted strings and whitespace
    private static List<string> SplitCommandArgs(string input)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return args;
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
        foreach (var movie in movies)
        {
            AnsiConsole.MarkupLine($"[bold green]{movie.Title}[/] ({movie.Year}) - {movie.Genre} - {movie.Rating}");
        }
    }

    private void AddCommand(string[] args)
    {
        if (args.Length != 4 && args.Length != 5)
        {
            AnsiConsole.MarkupLine("[red]Invalid number of arguments for /add command[/]");
        }
        var movie = new Movie(args[0], int.Parse(args[1]), args[2], int.Parse(args[3]), args[4] ?? null);
        if (_database.AddMovie(movie))
            AnsiConsole.MarkupLine($"[green]Movie {movie.Title} added successfully.[/]");
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

    public bool AddMovie(Movie movie)
    {
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
            INSERT INTO movies (title, year, genre, rating_id, imdb_url)
            VALUES (@title, @year, @genre, @rating_id, @imdb_url);
         """;
            command.Parameters.AddWithValue("@title", movie.Title);
            command.Parameters.AddWithValue("@year", movie.Year);
            command.Parameters.AddWithValue("@genre", movie.Genre);
            command.Parameters.AddWithValue("@rating_id", movie.RatingId);
            command.Parameters.AddWithValue("@imdb_url", movie.ImdbUrl);
            command.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(string.Format("[red]Error adding movie: {0}[/]", ex.Message));
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // Checkpoint the database to ensure all changes are written to disk
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }

        _connection.Dispose();
        _disposed = true;
    }
}

record Movie(string Title, int Year, string Genre, int RatingId, string? ImdbUrl = null);
record MovieListItem(string Title, int Year, string Genre, string Rating, string? ImdbUrl = null);