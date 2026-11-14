# Visual Basic in Passenger

Where Visual Basic (.NET) stands in Passenger, what was changed to get there, and what is still to do. Upstream's
request is Zaneris/passenger#1552 ("F# and VB.NET"), which the owner put in the Backlog as not planned; this work lives
on the `vb-net` branch of the fork.

## What works

**The language itself (stage 1).** A `.vb` file is a language of its own:

- the status bar names it Visual Basic, and the Explorer already had its icon;
- it is coloured: comments (`'` and `REM`), strings with their `""` escapes, numbers (`&HFF`, `1.5E3`, type suffixes),
  date literals (`#1/31/2026#`), the directives (`#Region`, `#If`), every reserved word in any letter case, the name a
  `Class`, `Module`, `Structure`, `Interface` or `Enum` declares, the type after `As`, `Of`, `New` and `Inherits`, and
  a `Sub`'s or `Function`'s name;
- Comment with Line Comment writes and removes an apostrophe;
- an apostrophe typed starts a comment, where every other language pairs it as a quote;
- a Markdown file's ```` ```vb ```` block is coloured as a `.vb` file is.

**Folding (the first part of stage 3).** A `.vb` file folds: a namespace, a type, a member and a property's or custom
event's accessors from the line that opens them to their `End` (the placeholder reads `...End Sub`), a lambda that goes
on past its line, `#Region` to `#End Region`, the `Imports`, a documentation comment and the comments a file begins
with. Collapse to Definitions, the fold settings and the folds a folder remembers treat them as C#'s.

**What was there before.** A `.vbproj` was already a project to Passenger: the workspace finds it, a solution takes it
with Visual Basic's project type, MSBuild's design-time build reads it, and Build and Run go through MSBuild as a C#
project's do. None of that was changed, and none of it has a test with a Visual Basic project yet.

## How it is built

| Part | Where | Why so |
|---|---|---|
| The language | `Language.VisualBasic` in `src/Passenger.Languages/Language.cs` | one entry, as every TextMate language is |
| The grammar | `src/Passenger.Languages/TextMate/Grammars/visualbasic.tmLanguage.json` | Passenger's own: see below |
| A Markdown block | `EmbeddedAs` in `Language.cs`, `meta.embedded.block.vs_net` in `ScopeColours.cs` | VS Code's Markdown grammar names Visual Basic `source.asp.vb.net` |
| The apostrophe | `TextEditing.Type`'s `comment`, given the editor's `LineCommentPrefix` | the editor names no language |
| The folds | `VisualBasicFolds` behind `FoldScan.VisualBasic` (`src/Passenger.Languages/Markup`) | a scan of lines off the UI thread, as XML's and JSON's: no parser is carried for it yet |
| The tests | `FileColoursTests` (a file's kinds, its comment, the apostrophe, Markdown), `EditingTests` (typing), `FoldingTests` (the folds) | |

**Why a grammar of Passenger's own.** Every other grammar here is VS Code's, unchanged. VS Code's grammar for `.vb`
(`extensions/vb`, converted from textmate/asp.vb.net.tmbundle) is classic ASP's: it knows no `Imports`, `Namespace`,
`Module`, `AndAlso`, `Overrides`, `Try` or `Async`, colours any name before a bracket as a function, and takes whatever
follows `as ` for a type. A file coloured by it looks broken. The grammar written instead is small (one rule per
kind of token, the reserved words from the language's specification) and needs no third-party notice.

**What the grammar leaves alone, on purpose.**

- A call is not coloured: `Name(…)` is an index too, and only a compiler can tell them apart.
- An interpolated string's holes (`$"… {x} …"`) are string-coloured.
- LINQ's query words (`From`, `Where`, `Select` in a query, `Group`, `Order By`) and the other contextual words that
  are common names (`Text`, `Key`, `Take`) are no keywords, so a variable named so is not miscoloured.
- A word after a dot or inside `[brackets]` is a name, never a keyword.

**How the folds are found.** A block is known by its line's first word past its attributes and modifiers, and an `End`
closes the nearest open block of its kind, dropping those opened since that have no `End` of their own: an
auto-property, an interface's member, a `MustOverride` one. Statement blocks (`If`, `For`, `Try`) do not fold. What a
line scan gets wrong: a declaration inside a string over several lines, and a lambda whose line is longer than 512
characters.

## What does not work yet

Nothing answers for a Visual Basic file beyond the editor: no completion, no errors, no Go to Declaration, no rename,
no Find Usages, no formatting, no `End If` written for you. The debugger stops at breakpoints in Visual
Basic code as far as the PDB carries it (untested), but evaluates expressions as C#.

## What stands in the way

**Roslyn's server carries no Visual Basic, and nuget.org has none that fits it.** Passenger's C# help is Roslyn's
language server (`roslyn-language-server.<rid>`, `RoslynServerVersion` in `Directory.Packages.props`). The package at
5.13.0-1.26514.10 holds `Microsoft.CodeAnalysis.CSharp*.dll` and no `Microsoft.CodeAnalysis.VisualBasic*.dll`. Visual
Basic's Features and Workspaces assemblies reach into Roslyn's internals, so they must be of the server's very build,
and nuget.org, the only source Passenger restores from, carries them at 5.9.0 and 5.12.0 but at no build a server
package matches (the servers there are 5.11.0, 5.12.0-1.x and 5.13.0-1.x). So stage 2 cannot simply add assemblies to
the carried server. The ways left, each a decision for the project's owner:

- **A language service of Passenger's own for Visual Basic**, a helper process holding Roslyn's public workspace API
  from nuget.org at one version (`Microsoft.CodeAnalysis.VisualBasic.Features`, `.Workspaces.MSBuild`): completion,
  diagnostics, navigation, rename and Find Usages are all public API there. It keeps every rule (nuget.org alone, MIT)
  and costs the most work: a second server beside Roslyn's, with its own project loading.
- **Visual Basic's assemblies of the server's build from the .NET team's own feed** (`dotnet-tools` on Azure DevOps),
  which breaks the rule that packages come from nuget.org alone, and still leaves open whether the server composes them
  and loads a `.vbproj`.
- **Waiting** for a server package and Visual Basic packages of one build on nuget.org.

The in-process parser is no obstacle: `Microsoft.CodeAnalysis.VisualBasic` 5.9.0, the version Passenger's C# parser is
at, is on nuget.org. Passenger's extension inside the server already exports its handlers for both
languages (`ExportCSharpVisualBasicLspServiceFactory`), and `ServerPlaces` already counts `.vb` among Roslyn's files.

**The App asks for C# by name.** `Language.CSharp` is compared in 19 places and `".cs"` is written in 56 files; the
in-process parser that folds, indents and helps typing is C#'s alone (`CSharpParsing`).

**The debugger's expressions are C#'s.** `CilExpressionCompiler` reads a method's debug information with
`isVisualBasicMethod: false`; Visual Basic needs Roslyn's Visual Basic expression compiler beside the C# one.

## The stages

1. **The language** (done): the entry, the grammar, comments, typing, tests.
2. **Language help** (blocked on the decision above): `.vb` files answered for by a Roslyn workspace, with completion,
   diagnostics, navigation, rename, Find Usages and semantic colours.
3. **The editor's own help**: folding (done); still to do, the block ends written as you type, indentation, Reformat
   Code, New Item's templates and the Tests window's finder.
4. **The debugger**: expressions in Watches, hover and conditional breakpoints compiled as Visual Basic.

## Building on a machine that has only the .NET 10 SDK

`global.json` asks for the .NET SDK 11.0.100. Moving Passenger to .NET 10 instead was tried on 2026-11-14 and is
possible but not clean. With `global.json` at 10.0.100, `TargetFramework` at `net10.0` and `AnalysisLevel` at 10.0 the
App builds once three things are worked around:

- `ProcessStartInfo.KillOnParentExit` and `StartDetached` (`Lifeline.cs`, `ChildProcesses.cs`) are .NET 11's: they
  are how no child outlives the IDE.
- `ProcessStartInfo.StartSuspended` and `SafeProcessHandle.Resume()` (`SuspendedLaunch.cs`) are .NET 11's: they are how
  the debugger holds a program at its start.
- The .NET 10 SDK's COM generator writes code for `IMetaDataTables2` that lacks `unsafe`; `unsafe` on the interface
  cures it.

The trial only stubbed the four APIs out, which leaves the debugger starting nothing held and children outliving the
IDE; a real move would write both again over the platforms' own calls, on Windows, macOS and Linux. Three test projects
are .NET 11's by their nature (`RuntimeAsyncConsoleApp`, `StateMachineAsyncConsoleApp`, `EfGraphs.Sqlite11`), and every
`packages.lock.json` would be rewritten. Nothing of the trial was kept.

What was done instead: the .NET 11 SDK installed beside the system's, touching neither it nor any other project.

```sh
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 11.0 --install-dir ~/.dotnet11
bash dotnet-install.sh --channel 10.0 --runtime dotnet --install-dir ~/.dotnet11   # for Passenger's net10.0 parts
export DOTNET_ROOT=~/.dotnet11 PATH=~/.dotnet11:$PATH                              # in this repository's shell only
```

The second line matters: `Passenger.Host`, the Roslyn extension and the tests' debuggees are `net10.0` and need a
.NET 10 runtime in the same folder as the SDK that runs them. On Arch and CachyOS the system's `dotnet-sdk` (10) and
the AUR's `dotnet-sdk-bin` (11) conflict, which is why the SDK is not installed through pacman; once `extra` carries
.NET 11, `dotnet-sdk` becomes 11 with `dotnet-sdk-10.0` beside it, and `~/.dotnet11` can be deleted.
