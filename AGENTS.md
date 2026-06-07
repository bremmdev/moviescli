# AGENTS.md

## Project type

Single-file .NET 10 app — one `Program.cs`, no `.csproj`. Dependencies are declared inline with `#:package` directives and restored automatically by the SDK on first run.

## How to run

```bash
dotnet run Program.cs
```

This launches an interactive REPL. On Windows, `movies.bat` opens the same command in a maximized Windows Terminal window.

## Runtime requirements

- .NET 10 SDK (supports file-based apps with `#:package`)
- `movies.db` (SQLite) must exist in the working directory — it is created automatically on first run
- No build step required; no separate restore step needed

## No test suite

There are no automated tests. Verification is manual: run the app and exercise commands in the REPL.

## Key constraints for edits

- All code lives in `Program.cs` as `file`-scoped classes — do not split into multiple files or add a `.csproj` unless explicitly asked.
- New NuGet dependencies go at the top of `Program.cs` as additional `#:package` lines, not in a project file.
- The SQLite database file (`movies.db`) must not be committed or overwritten by tooling.

## Piping commands (non-interactive testing)

The app detects redirected stdin and falls back to `Console.ReadLine()`, so commands can be piped in for scripted checks:

```bash
echo /list | dotnet run Program.cs
printf "/add \"Test Movie\" 2024 Drama 3\n/list\n/exit\n" | dotnet run Program.cs
```
