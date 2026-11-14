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
| The tests | `FileColoursTests` (a file's kinds, its comment, the apostrophe, Markdown), `EditingTests` (typing) | |

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

## What does not work yet

Nothing answers for a Visual Basic file beyond the editor: no completion, no errors, no Go to Declaration, no rename,
no Find Usages, no folding, no formatting, no `End If` written for you. The debugger stops at breakpoints in Visual
Basic code as far as the PDB carries it (untested), but evaluates expressions as C#.

## What stands in the way

**Roslyn's server carries no Visual Basic.** Passenger's C# help is Roslyn's language server
(`roslyn-language-server.<rid>`, `RoslynServerVersion` in `Directory.Packages.props`). The package at 5.13 holds
`Microsoft.CodeAnalysis.CSharp*.dll` and no `Microsoft.CodeAnalysis.VisualBasic*.dll`, so stage 2 has to bring Visual
Basic's assemblies into the server's folder at the server's own version, and it is not known that the server then
composes them and loads a `.vbproj`. Passenger's extension inside the server already exports its handlers for both
languages (`ExportCSharpVisualBasicLspServiceFactory`), and `ServerPlaces` already counts `.vb` among Roslyn's files.

**The App asks for C# by name.** `Language.CSharp` is compared in 19 places and `".cs"` is written in 56 files; the
in-process parser that folds, indents and helps typing is C#'s alone (`CSharpParsing`).

**The debugger's expressions are C#'s.** `CilExpressionCompiler` reads a method's debug information with
`isVisualBasicMethod: false`; Visual Basic needs Roslyn's Visual Basic expression compiler beside the C# one.

## The stages

1. **The language** (done): the entry, the grammar, comments, typing, tests.
2. **Language help through Roslyn's server**: Visual Basic's assemblies in the server, `.vb` files opened in it,
   completion, diagnostics, navigation, rename, Find Usages, semantic colours.
3. **The editor's own help**: folding, the block ends written as you type, indentation, Reformat Code, New Item's
   templates, the Tests window's finder.
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
