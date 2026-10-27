# CompleteGit

CompleteGit is a desktop Git client for Windows (C# / WPF / .NET 10) with one defining feature:
**every git command the UI runs is executed through a built-in console pane, and you can type
your own commands into that same console.**

## Run

```
C:\projects\CompleteGit\publish\CompleteGit.exe
```

Or from source:

```
cd C:\projects\CompleteGit
dotnet run
```

Rebuild the published exe after changes:

```
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

Requires git on PATH (or set the path via Repository ▾ → Git executable…) and the .NET 10 runtime. Building needs NuGet access for the AvalonDock docking library.

## Layout

Every panel is dockable (AvalonDock). By default Branches sit in a full-height column on the left; on the right the log is on top, and below it the Changes panel (unstaged over staged, with the commit box) sits beside the Compare view, which takes all remaining width. The Console runs along the bottom.

Drag a panel by its title bar to move it: drop it on one of the arrows that appear to dock it on that side, drop it on the middle of another panel to tab it with that panel, or drop it anywhere else to float it as its own window. The pin icon tucks a panel away to the edge (it slides out on hover). The X hides a panel; bring it back from the toolbar's View ▾ menu, which also has Reset layout. The arrangement is saved to `%APPDATA%\CompleteGit\layout.xml` when the app closes.

| Area | What it does |
|---|---|
| Toolbar | Repository (open / clone / init / recent), Refresh, Fetch, Pull (split button), Push ▾, Branch ▾, Stash ▾, Terminal, Explorer. Current branch and upstream tracking on the right. |
| Pull / Push buttons | Split buttons. The main half runs the last chosen type and its label says which one (`Pull (merge)`, `Pull --rebase`, `Pull --ff-only`; `Push`, `Push --force-with-lease`, `Push --tags`). The ▾ half opens the list with the current type ticked; picking one runs it and makes it the new default, remembered in settings. |
| Branches (left) | Local branches (grouped by `/`), remotes (collapsed by default), tags, stashes. Double-click to checkout; right-click for merge, rebase, push, rename, delete, set upstream, apply/pop/drop stash… Ctrl+click or Shift+click selects several stashes; right-click then offers "Drop N selected stashes" (or press Delete), and the Stashes heading has "Drop all stashes". |
| Log | Commit graph with lanes, ref badges, filter box (Ctrl+F), branch scope (Current branch / Local branches / All branches), Load more. The default, Local branches, shows your local branches, the remote branches they track, and tags, so remote-only branches you have never checked out stay out of the graph. First row is the working tree. Right-click: checkout, branch/tag here, cherry-pick, revert, reset soft/mixed/hard, copy hash. |
| Working tree | Unstaged / Staged lists. Double-click or right-click to stage, unstage, discard, resolve conflicts (ours/theirs), add to .gitignore, open, copy path. Commit box with Amend, Commit, Commit & Push (Ctrl+Enter). |
| Commit panel | Selecting a commit shows hash, author, full message and changed files. |
| Diff | Side-by-side by default (toggle to unified). For an unstaged file the left pane is the index, the right pane is your working copy; for a staged file it is HEAD vs index. See "Pulling lines across" below. |
| Console | Every command: blue = actions, dim blue = background queries (status/log/diff refreshes), orange = stderr, red = failures with exit code, green = timing. Type your own commands at the prompt. |

## Console

* The prompt runs your line through **pwsh** (default), **powershell** or **cmd** — click the `Shell:` button to cycle.
* `cd <dir>` changes the console directory, `clear`/`cls` clears, Up/Down browse history, Ctrl+L clears, Ctrl+C or **Stop** kills a running command.
* After any typed command the UI refreshes, so `git checkout -b foo` in the console updates the branch tree immediately.
* **Show background queries** hides the read-only refresh traffic so your own commands stand out.
* Commands run one at a time, so output never interleaves. Git is launched with `GIT_TERMINAL_PROMPT=0`; credential prompts come from Git Credential Manager's own window.

## Pulling lines across

Every changed row in the diff has arrows in the middle gutter:

| View | Arrow | Effect | Git command issued |
|---|---|---|---|
| Unstaged (index ◀▶ working tree) | ◀ | stage that row's change into the index | `git apply --cached -` with a patch containing only that line |
| Unstaged | ▶ | discard that row from the working copy (asks first) | `git apply -R -` |
| Staged (HEAD ◀▶ index) | ▶ | unstage that row | `git apply --cached -R -` |

A row is one change: for a modified line that is the removal on the left plus the replacement on the right. Click several rows (Ctrl/Shift-click) and use **Stage lines** / **Discard lines** / **Unstage lines** in the header, or an arrow on any selected row, to move them together. **Stage hunk** / **Unstage hunk** / **Discard hunk** still move whole hunks. Copy left or right side from the context menu (Ctrl+C copies the right side). The scroll position is kept after each operation so you can work down a file.

## Dialogs

Confirmations and inputs are drawn inside the main window as a dimmed overlay card: no system sound, no extra window, no focus stealing. Enter confirms, Escape cancels, clicking the backdrop cancels. Destructive confirmations have a red button.

## Merge / rebase / cherry-pick in progress

A banner appears with **Continue**, **Skip** and **Abort**. Conflicted files show as `U` in the Unstaged list; right-click them to resolve with ours/theirs or mark resolved after editing.

## Settings

`%APPDATA%\CompleteGit\settings.json` — last/recent repositories, shell, git path, window and splitter sizes.

## Source map

| File | Role |
|---|---|
| `Shell/CommandRunner.cs` | The single funnel for every process. Serialises commands, echoes them, streams output, handles `cd`/`clear`, wraps PowerShell so errors come out as text. |
| `Git/GitRepo.cs` | Typed git operations (queries are "quiet": only summarised in the console). |
| `Git/GraphBuilder.cs` | Lane assignment for the commit graph. |
| `Controls/GraphCell.cs` | Draws one graph row. |
| `Controls/ConsolePane.*` | Console output + input. |
| `Controls/DiffView.*` | Diff rendering, hunk patch construction. |
| `MainWindow.*` | Layout, refresh pipeline, all actions and context menus. |
| `Controls/DialogHost.*` | In-window overlay replacing MessageBox and input windows (`Dlg.Confirm` / `Dlg.Info` from anywhere). |
| `Assets/CompleteGit.ico` | App icon (generated by a GDI+ script; sizes 16–256). |
| `Theme.xaml` | Dark theme. |
