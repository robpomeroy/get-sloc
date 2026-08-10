<#
.SYNOPSIS
    Recursively counts source lines of code (SLOC) in PowerShell files.

.DESCRIPTION
    Get-SLOC parses each .ps1/.psm1/.psd1 file under the given path and counts
    the number of non-blank lines that contain at least one non-comment token.

    Counting is token-based rather than text-based: a line is counted as SLOC
    if and only if it is non-blank and is spanned by a non-comment token. This
    means:
      - Blank lines are never counted (including blank lines inside here-strings).
      - Comment-only lines (single-line hash comments and block comments) are excluded.
      - Multi-line strings and here-strings are counted on every non-blank line
        they span.

    The count is derived in a single pass over the parser tokens, so it avoids
    the per-line token re-scans of a naive implementation.

.PARAMETER Path
    The directory (or file) to scan. Defaults to the current directory.

.PARAMETER ExcludeDirectories
    Names of directories to skip during the recursive scan. Matching is
    case-insensitive and applies to any directory segment in a file's path.

.EXAMPLE
    Get-SLOC

    Counts SLOC for all PowerShell files under the current directory.

.EXAMPLE
    Get-SLOC -Path C:\Repos\DPS -ExcludeDirectories @('.venv', '.vscode', 'docs', 'logs', 'Modules', 'venv')

    Counts SLOC under C:\Repos\DPS, skipping the listed directories.

.OUTPUTS
    System.Management.Automation.PSCustomObject
        One object per file with File (full path) and SLOC (line count).
        The results are sorted by SLOC descending and displayed as a table,
        followed by a total line.

.NOTES
    Requires Windows PowerShell 5.1 or later.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Path = '.',

    [string[]]$ExcludeDirectories = @()
)

function Get-SLOC {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [string]$Path = '.',

        [string[]]$ExcludeDirectories = @()
    )

    # Normalize excluded directory names for quick, case-insensitive comparison.
    $excludeSet = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    foreach ($d in $ExcludeDirectories) {
        if ($d) { [void]$excludeSet.Add($d) }
    }

    # Extensions we care about, matched case-insensitively.
    $extSet = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    foreach ($ext in '.ps1', '.psm1', '.psd1') {
        [void]$extSet.Add($ext)
    }

    # Enumerate files. -Include with -Recurse is slow (wildcard against full
    # path for every file), so use -File -Recurse and filter by extension here.
    $files = Get-ChildItem -Path $Path -Recurse -File |
        Where-Object {
            $extSet.Contains($_.Extension) -and
            -not (
                (Split-Path -Path $_.FullName -Parent) -split '[\\/]' |
                    Where-Object { $excludeSet.Contains($_) }
            )
        }

    $results = foreach ($file in $files) {
        $tokens = $null
        $errors = $null

        [System.Management.Automation.Language.Parser]::ParseFile(
            $file.FullName,
            [ref]$tokens,
            [ref]$errors
        ) | Out-Null

        # Read the file once. We need the raw lines to exclude blank lines
        # (including blank lines inside here-strings / multi-line strings),
        # matching the original script's semantics. File.ReadAllLines is much
        # faster than the Get-Content cmdlet.
        $lines = [System.IO.File]::ReadAllLines($file.FullName)

        # SLOC = number of distinct, non-blank line numbers spanned by code
        # tokens. Only real code tokens count: Comment, NewLine, EndOfInput and
        # LineContinuation are excluded (NewLine and LineContinuation extents
        # span the line they terminate, so they must not be treated as code).
        # Comment-only lines have no code tokens, so they are excluded
        # automatically. This is a single O(tokens) pass instead of the
        # original O(lines x tokens) per-line re-scan.
        #
        # Micro-optimization: caching $t.Kind / $t.Extent in locals avoids
        # repeated property dispatch. (A bool[] was tried but New-Object
        # overhead made it slower than the HashSet.)
        $slocLines = [System.Collections.Generic.HashSet[int]]::new()

        foreach ($t in $tokens) {
            $kind = $t.Kind
            if ($kind -eq 'Comment' -or
                $kind -eq 'NewLine' -or
                $kind -eq 'EndOfInput' -or
                $kind -eq 'LineContinuation') { continue }

            $extent = $t.Extent
            for ($i = $extent.StartLineNumber; $i -le $extent.EndLineNumber; $i++) {
                if (-not [string]::IsNullOrWhiteSpace($lines[$i - 1])) {
                    [void]$slocLines.Add($i)
                }
            }
        }

        [pscustomobject]@{
            File = $file.FullName
            SLOC = $slocLines.Count
        }
    }

    $results | Sort-Object SLOC -Descending | Format-Table -AutoSize

    "`nTotal SLOC: $((($results | Measure-Object SLOC -Sum).Sum))"
}

Get-SLOC -Path $Path -ExcludeDirectories $ExcludeDirectories
