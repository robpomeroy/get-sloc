using System.Management.Automation.Language;

namespace GetSLOC;

/// <summary>
/// Command-line port of Get-SLOC.ps1. Recursively counts source lines of
/// code (SLOC) in PowerShell files using the same token-based algorithm as
/// the PowerShell script.
/// </summary>
internal static class Program
{
    private static readonly HashSet<TokenKind> ExcludedKinds = new()
    {
        TokenKind.Comment,
        TokenKind.NewLine,
        TokenKind.EndOfInput,
        TokenKind.LineContinuation,
    };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1",
        ".psm1",
        ".psd1",
    };

    private static int Main(string[] args)
    {
        string path = ".";
        var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-Path":
                case "--path":
                    if (i + 1 < args.Length)
                    {
                        path = args[++i];
                    }
                    else
                    {
                        FailFast($"Option '{args[i]}' requires a value.");
                    }
                    break;
                case "-ExcludeDirectories":
                case "--exclude":
                {
                    string option = args[i];
                    bool consumed = false;
                    while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    {
                        excludeDirs.Add(args[++i]);
                        consumed = true;
                    }
                    if (!consumed)
                    {
                        FailFast($"Option '{option}' requires at least one value.");
                    }
                    break;
                }
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                default:
                    // Treat a bare, non-flag argument as the path.
                    if (!args[i].StartsWith('-')) { path = args[i]; }
                    break;
            }
        }

        if (!Directory.Exists(path))
        {
            Console.Error.WriteLine($"Path not found: {path}");
            return 1;
        }

        var results = new List<(string File, int Sloc)>();

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
        };

        foreach (string file in Directory.EnumerateFiles(path, "*", options))
        {
            string ext = Path.GetExtension(file);
            if (!Extensions.Contains(ext)) { continue; }

            if (IsExcluded(file, excludeDirs)) { continue; }

            try
            {
                results.Add((file, CountSloc(file)));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                                       or IOException)
            {
                Console.Error.WriteLine($"Warning: skipping '{file}': {ex.Message}");
            }
        }

        foreach (var r in results.OrderByDescending(r => r.Sloc))
        {
            Console.WriteLine($"{r.Sloc,8}  {r.File}");
        }

        int total = results.Sum(r => r.Sloc);
        Console.WriteLine();
        Console.WriteLine($"Total SLOC: {total}");

        return 0;
    }

    /// <summary>Prints an error message and a usage hint, then exits with a non-zero code.</summary>
    private static void FailFast(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        Console.Error.WriteLine("Run with --help for usage.");
        Environment.Exit(1);
    }

    private static bool IsExcluded(string fullPath, HashSet<string> excludeDirs)
    {
        if (excludeDirs.Count == 0) { return false; }

        string? dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir)) { return false; }

        foreach (string segment in dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (excludeDirs.Contains(segment)) { return true; }
        }
        return false;
    }

    private static int CountSloc(string file)
    {
        Token[] tokens;
        ParseError[] errors;

        // ParseFile reads the file itself; we also read it once for the raw
        // lines so we can exclude blank lines (including blank lines inside
        // here-strings / multi-line strings), matching the PS script.
        Parser.ParseFile(file, out tokens, out errors);

        string[] lines = File.ReadAllLines(file);

        var slocLines = new HashSet<int>();

        foreach (Token t in tokens)
        {
            if (ExcludedKinds.Contains(t.Kind)) { continue; }

            for (int i = t.Extent.StartLineNumber; i <= t.Extent.EndLineNumber; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i - 1]))
                {
                    slocLines.Add(i);
                }
            }
        }

        return slocLines.Count;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Get-SLOC - counts source lines of code in PowerShell files.

            Usage:
              Get-SLOC [Path] [-Path <dir>] [-ExcludeDirectories <dir> [<dir> ...]]

            Options:
              -Path <dir>                 Directory to scan (default: current directory).
              -ExcludeDirectories <dirs>  Directory names to skip (case-insensitive).
              -h, --help                  Show this help.

            Counts .ps1/.psm1/.psd1 files. A line counts as SLOC if it is
            non-blank and spanned by a non-comment token.
            """);
    }
}
