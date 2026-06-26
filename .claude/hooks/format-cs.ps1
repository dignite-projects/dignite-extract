# PostToolUse hook (matcher: Edit|Write|MultiEdit).
# Fast whitespace-format of the just-edited C# file via `dotnet format whitespace`.
#
# Hard-won design notes (verified empirically on this box, dotnet 10.0.301):
#  - $CLAUDE_FILE_PATHS does NOT exist; the tool-call payload arrives as JSON on stdin.
#  - jq is not installed here; parse with ConvertFrom-Json.
#  - `dotnet format --include` matches paths relative to the PROCESS cwd — NOT absolute
#    paths and NOT project-relative paths. An absolute path silently formats nothing.
#    So we force cwd to the repo root and pass a repo-relative path.
#  - Target the file's OWNING .csproj, never the 31-project root .slnx, or every edit
#    pays a ~12s whole-solution load. One project loads in ~1-2s.
#  - `whitespace` skips analyzer/style passes for a fast save-time format.
$ErrorActionPreference = 'SilentlyContinue'

$raw = [Console]::In.ReadToEnd()
if (-not $raw) { exit 0 }
try { $path = ($raw | ConvertFrom-Json).tool_input.file_path } catch { exit 0 }
if (-not $path) { exit 0 }
if ($path -notlike '*.cs') { exit 0 }
if ($path -match '[\\/](obj|bin|Migrations)[\\/]') { exit 0 }   # never reformat generated / migration files
if (-not (Test-Path -LiteralPath $path)) { exit 0 }

# Repo root = two levels up from .claude/hooks/ — independent of the hook's cwd.
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# Walk up from the edited file to its nearest *.csproj.
$dir = Split-Path -Parent $path
$proj = $null
while ($dir) {
    $hit = Get-ChildItem -LiteralPath $dir -Filter *.csproj -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { $proj = $hit.FullName; break }
    $parent = Split-Path -Parent $dir
    if ($parent -eq $dir) { break }
    $dir = $parent
}
if (-not $proj) { exit 0 }

# --include resolves relative to cwd: force cwd to repo root, pass a repo-relative path.
# (Windows PowerShell 5.1 lacks [Path]::GetRelativePath, so strip the repo prefix by hand.)
Set-Location -LiteralPath $repo
if (-not $path.StartsWith($repo, [System.StringComparison]::OrdinalIgnoreCase)) { exit 0 }
$rel = $path.Substring($repo.Length).TrimStart('\', '/') -replace '\\', '/'
if (-not $rel) { exit 0 }

dotnet format whitespace "$proj" --include "$rel" --no-restore --verbosity quiet 2>$null
exit 0
