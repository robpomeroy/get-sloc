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
                    // Stop on any '-' prefixed token (standard CLI behavior),
                    // so typos like '-Pth' are not silently consumed as
                    // directory names. Leading-dash directory names can still
                    // be passed via a non-dash-prefixed path such as './-dir'.
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
                    // Unknown flag starting with '-' -> likely a typo. Fail
                    // fast rather than silently ignoring it (which could scan
                    // the wrong path). A bare, non-flag argument is the path.
                    if (args[i].StartsWith('-'))
                    {
                        FailFast($"Unknown option '{args[i]}'.");
                    }
                    else
                    {
                        path = args[i];
                    }
                    break;
            }
        }

        if (Directory.Exists(path))
        {
            CountDirectory(path, excludeDirs);
        }
        else if (File.Exists(path))
        {
            CountSingleFile(path, excludeDirs);
        }
        else
        {
            Console.Error.WriteLine($"Path not found: {path}");
            return 1;
        }

        return 0;
    }

    /// <summary>Counts SLOC for every supported file under a directory tree.</summary>
    private static void CountDirectory(string path, HashSet<string> excludeDirs)
    {
        var results = new List<(string File, int Sloc)>();

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
        };

        // EnumerateFiles is lazy, so exceptions (e.g. UnauthorizedAccessException,
        // IOException, DirectoryNotFoundException) can surface during iteration
        // even with IgnoreInaccessible. Guard the whole enumeration so a single
        // inaccessible subtree warns and returns partial results instead of
        // terminating with an unhandled exception.
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", options))
            {
                string ext = Path.GetExtension(file);
                if (!Extensions.Contains(ext)) { continue; }

                if (IsExcluded(file, excludeDirs)) { continue; }

                if (TryCountSloc(file, out int sloc))
                {
                    results.Add((file, sloc));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                   or IOException
                                   or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"Warning: enumeration of '{path}' failed partway: {ex.Message}");
        }

        PrintResults(results);
    }

    /// <summary>Counts SLOC for a single explicitly-specified file.</summary>
    private static void CountSingleFile(string file, HashSet<string> excludeDirs)
    {
        if (!Extensions.Contains(Path.GetExtension(file)))
        {
            Console.Error.WriteLine($"Warning: '{file}' is not a .ps1/.psm1/.psd1 file; nothing to count.");
            return;
        }

        if (IsExcluded(file, excludeDirs)) { return; }

        if (TryCountSloc(file, out int sloc))
        {
            PrintResults(new[] { (file, sloc) });
        }
    }

    /// <summary>Try to count a single file, warning and returning false on failure.</summary>
    private static bool TryCountSloc(string file, out int sloc)
    {
        try
        {
            sloc = CountSloc(file);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                   or IOException)
        {
            Console.Error.WriteLine($"Warning: skipping '{file}': {ex.Message}");
            sloc = 0;
            return false;
        }
    }

    private static void PrintResults(IEnumerable<(string File, int Sloc)> results)
    {
        foreach (var r in results.OrderByDescending(r => r.Sloc))
        {
            Console.WriteLine($"{r.Sloc,8}  {r.File}");
        }

        int total = results.Sum(r => r.Sloc);
        Console.WriteLine();
        Console.WriteLine($"Total SLOC: {total}");
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
              Get-SLOC [Path] [-Path <path>] [-ExcludeDirectories <dir> [<dir> ...]]

            Options:
              -Path <path>                File or directory to scan (default: current directory).
              -ExcludeDirectories <dirs>  Directory names to skip (case-insensitive).
              -h, --help                  Show this help.

            Counts .ps1/.psm1/.psd1 files. A line counts as SLOC if it is
            non-blank and spanned by a non-comment token. Passing a single
            file counts just that file; passing a directory scans it
            recursively.
            """);
    }
}
