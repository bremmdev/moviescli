#:package Microsoft.Data.Sqlite@10.0.8
#:package Spectre.Console@0.55.2

using System.Text;
using Microsoft.Data.Sqlite;
using Spectre.Console;

using var database = new Database("Data Source=movies.db");
var handler = new CommandHandler(database);
IEnumerable<string> availableCommands = handler.GetCommands();
var reader = new CommandLineReader(availableCommands);
if (!Console.IsInputRedirected)
    Console.TreatControlCAsInput = true; // Let the reader handle Control-C as a clean exit.

// Show welcome message
try
{
    int count = database.GetMovieCount();
    AnsiConsole.MarkupLine($"[bold green]Welcome to MovieCLI![/] Your collection has [bold]{count}[/] {(count == 1 ? "movie" : "movies")}.");
    AnsiConsole.MarkupLine("[grey]Type [blue]/help[/] to see available commands.[/]");
}
catch
{
    AnsiConsole.MarkupLine("[red]Error getting movie count from database. Please check if the database file exists and is readable.[/]");
    return;
}

while (true)
{
    AnsiConsole.Markup("[grey]> [/]");
    var input = reader.ReadLine();

    // If the user pressed Control-C, exit the application
    if (input == null)
    {
        handler.HandleCommand("/exit");
        break;
    }

    if (!input.Trim().StartsWith('/'))
    {
        AnsiConsole.MarkupLine("[red]Invalid command. Commands must start with '/'[/]");
        continue;
    }

    // If the command returns false, exit the main loop to cleanly exit the application
    // Needed so WAL journal is cleanly checked in
    if (!handler.HandleCommand(input.Trim()))
        break;
}

// Command line reader with history navigation via Arrow keys
file sealed class CommandLineReader
{
    private readonly List<string> _history = [];
    private int _historyIndex;
    private readonly List<string> _commands;

    public CommandLineReader(IEnumerable<string> commands)
    {
        _commands = commands.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? ReadLine()
    {
        // If the input is redirected (e.g. from a file or pipe) then use the default Console.ReadLine()
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        var buffer = new StringBuilder();
        _historyIndex = _history.Count;
        string? draft = null;
        string? tabPrefix = null;
        int tabIndex = -1;
        IReadOnlyList<string>? tabMatches = null;

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    // Return null to indicate that the user pressed Control-C so the main loop can exit
                    return null;

                case ConsoleKey.Enter:
                    Console.WriteLine();
                    AddToHistory(buffer.ToString());
                    return buffer.ToString();

                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b"); // write backspace character to erase the last character
                        ResetTabCycle(ref tabPrefix, ref tabIndex, ref tabMatches);
                    }
                    break;

                case ConsoleKey.Tab:
                    var commandPrefix = GetCommandPrefix(buffer);
                    if (tabPrefix is not null
                        && tabMatches is not null
                        && commandPrefix.StartsWith(tabPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        tabIndex = (tabIndex + 1) % tabMatches.Count;
                    }
                    else
                    {
                        tabPrefix = commandPrefix;
                        tabMatches = _commands
                            .Where(c => c.StartsWith(tabPrefix, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        tabIndex = 0;
                    }

                    if (tabMatches.Count > 0)
                        ReplaceCommand(buffer, tabMatches[tabIndex]);
                    break;

                case ConsoleKey.UpArrow when _history.Count > 0:
                    ResetTabCycle(ref tabPrefix, ref tabIndex, ref tabMatches);
                    if (_historyIndex == _history.Count)
                    {
                        draft = buffer.ToString();
                        _historyIndex = _history.Count - 1;
                    }
                    else if (_historyIndex > 0)
                    {
                        _historyIndex--;
                    }
                    ReplaceBuffer(buffer, _history[_historyIndex]);
                    break;

                case ConsoleKey.DownArrow when _history.Count > 0:
                    ResetTabCycle(ref tabPrefix, ref tabIndex, ref tabMatches);
                    if (_historyIndex < _history.Count - 1)
                    {
                        _historyIndex++;
                        ReplaceBuffer(buffer, _history[_historyIndex]);
                    }
                    else if (_historyIndex == _history.Count - 1)
                    {
                        _historyIndex = _history.Count;
                        ReplaceBuffer(buffer, draft ?? "");
                    }
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                        ResetTabCycle(ref tabPrefix, ref tabIndex, ref tabMatches);
                    }
                    break;
            }
        }
    }

    private static string GetCommandPrefix(StringBuilder buffer)
    {
        var text = buffer.ToString();
        var space = text.IndexOf(' ');
        return space >= 0 ? text[..space] : text;
    }

    private static void ReplaceCommand(StringBuilder buffer, string command)
    {
        var text = buffer.ToString();
        var space = text.IndexOf(' ');
        var suffix = space >= 0 ? text[space..] : "";
        ReplaceBuffer(buffer, command + suffix);
    }

    private static void ResetTabCycle(ref string? tabPrefix, ref int tabIndex, ref IReadOnlyList<string>? tabMatches)
    {
        tabPrefix = null;
        tabIndex = -1;
        tabMatches = null;
    }

    private static void ReplaceBuffer(StringBuilder buffer, string text)
    {
        while (buffer.Length > 0)
        {
            buffer.Length--;
            Console.Write("\b \b");
        }

        buffer.Append(text);
        Console.Write(text);
    }

    private void AddToHistory(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        if (_history.Count > 0 && _history[^1] == line)
            return;

        _history.Add(line);
    }
}

file sealed class CommandHandler
{
    private readonly Database _database;
    // Dictionary of commands and their corresponding actions, case-insensitive
    private readonly Dictionary<string, Func<string[], bool>> _commands;

    public CommandHandler(Database database)
    {
        _database = database;
        _commands = new(StringComparer.OrdinalIgnoreCase)
        {
            ["/add"] = (args) => { AddCommand(args); return true; },
            ["/delete"] = (args) => { DeleteCommand(args); return true; },
            ["/export"] = (args) => { ExportCommand(args); return true; },
            ["/find"] = (args) => { FindCommand(args); return true; },
            ["/help"] = _ => { HelpCommand(); return true; },
            ["/import"] = (args) => { ImportCommand(args[0]); return true; },
            ["/list"] = _ => { ListCommand(); return true; },

            // Aliases
            ["/exit"] = _ => false,
            ["/ls"] = _ => { ListCommand(); return true; },
            ["/quit"] = _ => false,
            ["/q"] = _ => false,
            ["/rm"] = (args) => { DeleteCommand(args); return true; },
        };
    }

    public IEnumerable<string> GetCommands()
    {
        return _commands.Keys;
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

    // Bool indicating success or failure
    private bool AddCommand(string[] args)
    {
        if (args.Length != 4 && args.Length != 5)
        {
            AnsiConsole.MarkupLine("[red]Invalid number of arguments for /add command[/]");
            return false;
        }

        if (!int.TryParse(args[1], out var year) || !int.TryParse(args[3], out var rating))
        {
            AnsiConsole.MarkupLine("[red]Invalid year or rating for /add command[/]");
            return false;
        }

        if (_database.MovieExists(args[0], year))
        {
            AnsiConsole.MarkupLine($"[red]Movie '{Markup.Escape(args[0])}' already exists.[/]");
            return false;
        }

        var imdbUrl = args.Length == 5 ? args[4] : null;
        var movie = new Movie(args[0], year, args[2], rating, imdbUrl);
        if (_database.AddMovie(movie))
        {
            AnsiConsole.MarkupLine($"[green]Movie '{Markup.Escape(movie.Title)}' added successfully.[/]");
            return true;
        }

        return false;
    }

    // We can delete by id or title
    private void DeleteCommand(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            AnsiConsole.MarkupLine("[red]Usage: /delete <id|title>[/]");
            return;
        }
        var arg = args[0];
        // Delete by id or title depending on the argument type
        string? deletedTitle = int.TryParse(arg, out var id)
            ? _database.DeleteMovie(id, null)
            : _database.DeleteMovie(null, arg);

        if (!String.IsNullOrWhiteSpace(deletedTitle))
            AnsiConsole.MarkupLine($"[green]Movie {Markup.Escape(deletedTitle)} deleted successfully.[/]");
        else
            AnsiConsole.MarkupLine("[red]Error deleting movie: invalid id or title[/]");
    }

    // Helper function to escape SQL literals
    private static string SqlLiteral(string value) => $"'{value.Replace("'", "''")}'";

    // Helper function to quote command arguments for /add exports
    private static string CommandLiteral(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    // 2 modes: 'add' mode (default) exports /add statements in a .txt, 'sql' mode exports sql code
    // The .txt file can be imported, the .sql file can be run against the db using the sqlite cli
    private void ExportCommand(string[] args)
    {
        if (args.Length != 1 || (args[0] != "sql" && args[0] != "text"))
        {
            AnsiConsole.MarkupLine("[red]Usage: /export sql or /export text[/]");
            return;
        }

        bool isSQL = args[0] == "sql";

        try
        {
            var ratings = _database.GetRatingsForExport();
            var movies = _database.GetMoviesForExport();

            var sb = new StringBuilder();

            if (movies.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]No movies found in database[/]");
                return;
            }

            if (isSQL)
            {
                foreach (var rating in ratings)
                {
                    sb.AppendLine($"INSERT OR IGNORE INTO ratings (id, rating) VALUES ({rating.Id}, {SqlLiteral(rating.RatingName)});");
                }

                sb.AppendLine();

                foreach (var movie in movies)
                {
                    sb.AppendLine($"INSERT OR REPLACE INTO movies (id, title, year, genre, rating_id, imdb_url) VALUES ({movie.Id}, {SqlLiteral(movie.Title)}, {movie.Year}, {SqlLiteral(movie.Genre)}, {movie.RatingId}, {SqlLiteral(movie.ImdbUrl ?? "")});");
                }
            }
            else
            {
                foreach (var movie in movies)
                {
                    sb.AppendLine($"/add {CommandLiteral(movie.Title)} {movie.Year} {CommandLiteral(movie.Genre)} {movie.RatingId} {CommandLiteral(movie.ImdbUrl ?? "")}");
                }
            }

            File.WriteAllText(isSQL ? "export.sql" : "export.txt", sb.ToString());
            AnsiConsole.MarkupLine($"[green]Database exported to {Markup.Escape(isSQL ? "export.sql" : "export.txt")}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error exporting database: {ex.Message}[/]");
        }
    }

    private void FindCommand(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            AnsiConsole.MarkupLine("[red]Usage: /find <id|title>[/]");
            return;
        }
        var arg = args[0];

        // Find by id or title depending on the argument type
        var movie = int.TryParse(arg, out var id)
            ? _database.GetMovie(id)
            : _database.GetMovie(arg);

        if (movie != null)
        {
            var table = new Table();

            // Add the headers based on the MovieListItem properties
            foreach (var header in typeof(MovieListItem).GetProperties())
            {
                table.AddColumn(header.Name.ToLower());
            }

            table.AddRow(movie.Id.ToString(), movie.Title, movie.Year.ToString(), movie.Genre, movie.Rating, movie.ImdbUrl ?? "");
            AnsiConsole.Write(table);
        }
        else
            AnsiConsole.MarkupLine("[red]Movie not found[/]");
    }

    private static void HelpCommand()
    {
        AnsiConsole.MarkupLine("""
            [bold green]Available commands:[/]
            [bold blue]/help[/] - Show this help message
            [bold blue]/list or /ls[/] - List all movies, usage: /list or /ls
            [bold blue]/add[/] - Add a new movie, usage: /add <title> <year> <genre> <rating> <imdb_url>
            [bold blue]/delete or /rm[/] - Delete a movie, usage: /delete <id|title> or /rm <id|title>
            [bold blue]/export[/] - Export the database to text file or SQL file, usage: /export [sql] or /export [text]
            [bold blue]/find[/] - Find a movie, usage: /find <id|title>
            [bold blue]/import[/] - Import data from an exported text file, usage: /import <path>
            [bold blue]/exit[/] - Exit the application
            [bold blue]/quit[/] - Exit the application
            [bold blue]/q[/] - Exit the application
        """);
    }

    // Import data from an exported text file
    private void ImportCommand(string path)
    {
        if (!File.Exists(path) || Path.GetExtension(path) != ".txt")
        {
            AnsiConsole.MarkupLine($"[red]File '{Markup.Escape(path)}' not found or is not a text file.[/]");
            return;
        }

        var lines = File.ReadAllLines(path);
        var successCount = 0;
        var errorCount = 0;

        foreach (var line in lines)
        {
            var parts = SplitCommandArgs(line.Trim());
            if (parts.Count == 0 || !parts[0].Equals("/add", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine($"[red]Invalid line: {Markup.Escape(line)}[/]");
                errorCount++;
                continue;
            }

            if (AddCommand(parts.Skip(1).ToArray()))
                successCount++;
            else
                errorCount++;
        }

        AnsiConsole.MarkupLine($"[green]{successCount} movies imported successfully, {errorCount} movies failed to import.[/]");
    }

    private void ListCommand()
    {
        var movies = _database.GetMovies();
        var table = new Table();

        // Add the headers based on the MovieListItem properties
        foreach (var header in typeof(MovieListItem).GetProperties())
        {
            table.AddColumn(header.Name.ToLower());
        }

        var ratingColors = new Dictionary<string, string>
        {
            ["Very Poor"] = "red",
            ["Poor"] = "orange",
            ["Average"] = "#FFD700",
            ["Good"] = "darkgreen",
            ["Excellent"] = "green"
        };

        foreach (var movie in movies)
        {
            var styledTitle = movie.Rating == "Excellent" ? $"[bold green]{movie.Title}[/]" : movie.Title;
            var ratingColor = ratingColors.TryGetValue(movie.Rating, out var color) ? color : "white";
            var styleRating = $"[bold {ratingColor}]{movie.Rating}[/]";
            table.AddRow(movie.Id.ToString(), styledTitle, movie.Year.ToString(), movie.Genre, styleRating, movie.ImdbUrl ?? "");
        }

        AnsiConsole.Write(table);
        string formattedCount = $"{movies.Count} {(movies.Count == 1 ? "movie" : "movies")} in collection.";
        AnsiConsole.MarkupLine($"[bold green]{formattedCount}[/]");
    }
};

file sealed class Database : IDisposable
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
        CheckpointWal();
    }

    private static int GetUserVersion(SqliteCommand command)
    {
        command.CommandText = "PRAGMA user_version;";
        var result = command.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    // TABLE OPERATIONS

    public bool MovieExists(string title, int year)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM movies
            WHERE LOWER(title) = LOWER(@title)
                AND year = @year
            LIMIT 1;
        """;
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@year", year);
        var result = command.ExecuteScalar();
        return result is not null;
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
            command.Parameters.AddWithValue("@imdb_url", movie.ImdbUrl ?? (object)DBNull.Value);
            command.ExecuteNonQuery();
            CheckpointWal();
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(string.Format("[red]Error adding movie: {0}[/]", ex.Message));
            return false;
        }
    }

    public string? DeleteMovie(int? id, string? title)
    {
        using var command = _connection.CreateCommand();

        try
        {
            if (!String.IsNullOrWhiteSpace(title))
            {
                command.CommandText = "DELETE FROM movies WHERE LOWER(title) = LOWER(@title) RETURNING title";
                command.Parameters.AddWithValue("@title", title);
            }
            else
            {
                command.CommandText = "DELETE FROM movies WHERE id = @id RETURNING title";
                command.Parameters.AddWithValue("@id", id);
            }
            var deletedTitle = command.ExecuteScalar() as string;
            if (deletedTitle is not null)
                CheckpointWal();

            return deletedTitle;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(string.Format("[red]Error deleting movie: {0}[/]", ex.Message));
            return null;
        }
    }

    public MovieListItem? GetMovie(string title, bool exactMatch = false)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = exactMatch
            ? """
                SELECT
                    m.id,
                    m.title,
                    m.year,
                    m.genre,
                    r.rating,
                    m.imdb_url
                FROM movies AS m
                INNER JOIN ratings AS r ON r.id = m.rating_id
                WHERE LOWER(m.title) = LOWER(@title)
                ORDER BY m.title, m.year
                LIMIT 1;
            """
            : """
                SELECT
                    m.id,
                    m.title,
                    m.year,
                    m.genre,
                    r.rating,
                    m.imdb_url
                FROM movies AS m
                INNER JOIN ratings AS r ON r.id = m.rating_id
                WHERE LOWER(m.title) LIKE LOWER(@title)
                ORDER BY m.title, m.year
                LIMIT 1;
            """;
        command.Parameters.AddWithValue("@title", exactMatch ? title : '%' + title + '%');
        using var result = command.ExecuteReader();
        return result.Read() ? new MovieListItem(
            result.GetInt32(0),
            result.GetString(1),
            result.GetInt32(2),
            result.GetString(3),
            result.GetString(4),
            result.IsDBNull(5) ? null : result.GetString(5)
        ) : null;
    }

    public MovieListItem? GetMovie(int id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT
                m.id,
                m.title,
                m.year,
                m.genre,
                r.rating,
                m.imdb_url
            FROM movies AS m
            INNER JOIN ratings AS r ON r.id = m.rating_id
            WHERE m.id = @id
            LIMIT 1;
        """;
        command.Parameters.AddWithValue("@id", id);
        using var result = command.ExecuteReader();
        return result.Read() ? new MovieListItem(
            result.GetInt32(0),
            result.GetString(1),
            result.GetInt32(2),
            result.GetString(3),
            result.GetString(4),
            result.IsDBNull(5) ? null : result.GetString(5)
        ) : null;
    }

    public int GetMovieCount()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM movies";
        var result = command.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    public List<MovieListItem> GetMovies()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT
                m.id,
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
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)
                )
            );
        }

        return movies;
    }

    public List<MovieItemForExport> GetMoviesForExport()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, year, genre, rating_id, imdb_url
            FROM movies
            ORDER BY title, year
            """;

        using var reader = command.ExecuteReader();
        var movies = new List<MovieItemForExport>();

        while (reader.Read())
        {
            var imdbUrlOrdinal = reader.GetOrdinal("imdb_url");
            movies.Add(
                new MovieItemForExport(
                    reader.GetInt32(reader.GetOrdinal("id")),
                    reader.GetString(reader.GetOrdinal("title")),
                    reader.GetInt32(reader.GetOrdinal("year")),
                    reader.GetString(reader.GetOrdinal("genre")),
                    reader.GetInt32(reader.GetOrdinal("rating_id")),
                    reader.IsDBNull(imdbUrlOrdinal) ? null : reader.GetString(imdbUrlOrdinal)
                )
            );
        }

        return movies;
    }

    public List<Rating> GetRatingsForExport()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, rating FROM ratings ORDER BY id";

        using var reader = command.ExecuteReader();
        var ratings = new List<Rating>();

        while (reader.Read())
        {
            ratings.Add(
                new Rating(
                    reader.GetInt32(reader.GetOrdinal("id")),
                    reader.GetString(reader.GetOrdinal("rating"))
                )
            );
        }

        return ratings;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        CheckpointWal();
        _connection.Dispose();
        _disposed = true;
    }

    private void CheckpointWal()
    {
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
    }
}

file sealed record Movie(string Title, int Year, string Genre, int RatingId, string? ImdbUrl = null);
file sealed record MovieListItem(int Id, string Title, int Year, string Genre, string Rating, string? ImdbUrl = null);

file sealed record MovieItemForExport(int Id, string Title, int Year, string Genre, int RatingId, string? ImdbUrl = null);

file sealed record Rating(int Id, string RatingName);