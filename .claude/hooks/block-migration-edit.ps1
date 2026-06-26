# PreToolUse hook (matcher: Edit|Write|MultiEdit).
# Blocks direct edits to EF Core migration files. Exit code 2 = block the tool call;
# stderr is fed back to Claude as the reason. Migrations must be regenerated via
# `dotnet ef migrations add`, never hand-edited.
#
# The tool-call payload arrives as JSON on stdin ($CLAUDE_FILE_PATHS does NOT exist).
# jq is unavailable here, so we parse with ConvertFrom-Json. The regex matches BOTH
# path separators for the Windows backslash paths Claude Code reports.
$raw = [Console]::In.ReadToEnd()
if (-not $raw) { exit 0 }
try { $path = ($raw | ConvertFrom-Json).tool_input.file_path } catch { exit 0 }
if (-not $path) { exit 0 }
if ($path -match '[\\/]Migrations[\\/]') {
    [Console]::Error.WriteLine('Blocked: regenerate via `dotnet ef migrations add` instead of editing migration files directly.')
    exit 2
}
exit 0
