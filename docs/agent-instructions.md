# Using BehaveDiff from a coding agent

BehaveDiff exposes three MCP tools through `behavediff mcp` (stdio):

| Tool | Does |
|---|---|
| `check_behavior_changes(intent, expect[]?, baseRef?)` | Builds base + working tree, runs the tests with capture on both, returns the report JSON (schema: [phase1-proposal.md](phase1-proposal.md) §6). Takes minutes; sends progress notifications. |
| `explain_difference(id)` | One difference from the latest run, with the complete base and current observations behind it (raw, before normalization). |
| `accept_difference(id, reason)` | Records that a difference is correct. Later runs report it as `Accepted`. The id must be in the latest report and the reason can't be empty. |

## Setup

Install the tool (from a clone of this repo until it is published):

```
dotnet pack src/BehaveDiff.Cli -c Release -o ./nupkg
dotnet tool install -g BehaveDiff --add-source ./nupkg
```

Register it with Claude Code, from the app repo you want checked:

```
claude mcp add behavediff -- behavediff mcp
```

The server checks the repo it was started in. To keep BehaveDiff's config out of that repo, point at a file elsewhere:

```
claude mcp add behavediff -- behavediff mcp --config C:\path\outside\the\repo\behavediff.yml
```

### Claude desktop app (Code tab)

The Code tab reads the same MCP config as the CLI (`~/.claude.json`, `.mcp.json`). Its Connectors UI is for hosted
services only, so a local server goes in a settings file. Without the CLI, quit the app and merge a top-level
`mcpServers` entry into `%USERPROFILE%\.claude.json` (don't replace the file):

```json
"mcpServers": {
  "behavediff": {
    "type": "stdio",
    "command": "C:\\Users\\<you>\\.dotnet\\tools\\behavediff.exe",
    "args": ["mcp", "--repo", "C:\\path\\to\\app", "--config", "C:\\path\\outside\\the\\repo\\behavediff.yml"]
  }
}
```

- Use the full path to `behavediff.exe`: the app doesn't read PowerShell profiles, so `.dotnet\tools` may not be
  on its PATH. Restart the app after changing PATH or this file.
- A user-level entry applies to every project, so pin `--repo` (and `--config`) to the app it should check.
- Same with the CLI: `claude mcp add -s user behavediff -- <path>\behavediff.exe mcp --repo <app> --config <yml>`.
- Check it connected: ask the session which MCP servers are available.

- Or let a script do the edit for one project only (backs up, validates, both path spellings):
  `pwsh -File etc/register-mcp.ps1 -ProjectPath C:path	opp -Config C:pathoutside	heepobehavediff.yml`

Without `behavediff init` (which gitignores `.behavediff/`), run artifacts and accepted differences are stored under the temp directory instead of the repo, so the working tree stays untouched.

## CLAUDE.md snippet

Paste this into the app repo's `CLAUDE.md` (or `CLAUDE.local.md`):

````markdown
## Behavior check (BehaveDiff)

This repo is checked with the `behavediff` MCP server. It runs the test suite against the base branch and
against the working tree and reports every difference in what the app did: HTTP responses, database writes,
test outcomes.

**When:** after changing application code, before saying the task is done. Again after every fix it leads to.
Skip it for changes that can't affect runtime behavior (docs, comments, test-only refactors).

**How:**
1. Call `check_behavior_changes` with `intent` describing the change in one sentence, and `expect` listing
   only what the change is *meant* to affect: triggers (`"POST /widgets"`, `"GET /widgets/*"`) and/or
   `"entity:<Entity>"` for database writes. Keep `expect` narrow; a broad rule hides real regressions.
2. `exitCode` 2: the comparison didn't run. `errors[]` names the tree (`current` = your changes) and stage.
   Fix build or test-run failures and check again.
3. Every `Unexpected` difference must end up fixed or accepted:
   - Use `explain_difference` to see the full before/after observations and the test that produced them.
   - If it's a regression, fix the code and check again.
   - If it's a correct consequence of the change, call `accept_difference` with a specific reason a
     reviewer can verify ("Description now defaults to empty string, as requested"), not "expected".
4. Look over the `Intended` differences too: they should match the intent. An intended rule that matched
   something surprising is still a bug.
5. A `test-result` difference from `Passed` to `Failed` is a broken test. Fix the code, or tell the user if
   the test itself must change.

**Never, without telling the user explicitly:** add or widen ignore rules (`ignore.*` in `.behavediff.yml`),
disable the self-noise check, edit `accepted.json` by hand, or change test projects to make differences
disappear.

**When reporting the task done**, include the final check result: unexpected (should be 0), intended, and
every difference you accepted, with its reason.
````

## The same from a terminal

```
behavediff run --intent "..." --expect "entity:Widget"
behavediff explain d-3f9a1c2e
behavediff accept d-3f9a1c2e --reason "..."
```
