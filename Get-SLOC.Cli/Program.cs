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
                    if (i + 1 < args.Length) { path = args[++i]; }
                    break;
                case "-ExcludeDirectories":
                case "--exclude":
                    while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    {
                        excludeDirs.Add(args[++i]);
                    }
                    break;
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

        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file);
            if (!Extensions.Contains(ext)) { continue; }

            if (IsExcluded(file, excludeDirs)) { continue; }

            int sloc = CountSloc(file);
            results.Add((file, sloc));
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

    private static bool IsExcluded(string fullPath, HashSet<string> excludeDirs)
    {
        if (excludeDirs.Count == 0) { return false; }

        foreach (string segment in fullPath.Split('\\', '/'))
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
