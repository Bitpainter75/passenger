# Visual Basic in Passenger

Where Visual Basic (.NET) stands in this Passenger, what was changed to get there, and what is still to do. Upstream
(Zaneris/passenger) put Visual Basic in its Backlog as not planned (#1552, "F# and VB.NET"); this fork carries it as
its own, with no pull request upstream.

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

**Block ends (stage 3).** Enter at the end of a line that opens a block puts the caret a level in and writes the
block's end below: `End Sub`, `End Function`, `End Class`, `End If` (after a line ending in `Then`), `Next`, `Loop`,
`End Select`, `End Try`, `End With`, `End Using`, `End SyncLock`, `End While`, `End Get`, `End Set`, `#End Region`,
and the `End Sub` or `End Function` of a lambda that goes on past its line. A block that has its end is only indented,
and one typed inside another of its kind gets its own end.

**Language help (stage 2).** Passenger's own language server for Visual Basic (`src/Passenger.VisualBasic`) answers
for every `.vb` file:

- the compiler's errors and warnings as you type, underlined and counted as C#'s are;
- completion, with each item's signature and documentation;
- a call's signatures after its bracket and each comma, the overload the compiler chose first, with the documentation
  of the method and of the parameter the caret is at;
- hover, Go to Declaration, Find Usages and the highlights of the name at the caret;
- Rename, across the project's files, open or not;
- File Structure, and Reformat Code in the file's own indentation.

**New types.** In a Visual Basic project, Add › Class/Interface offers Class, Module, Interface, Structure and Enum
and makes a `.vb` file (`Public Module Basket` … `End Module`), with no `Namespace` statement, as the project's root
namespace holds every file of it. New Item lists the same five as Passenger's own beside dotnet new's, which for Visual
Basic are only its test items.

**Tests.** A Visual Basic test project's tests run and are listed with their files and lines, their failures with the
compiler's own places: checked on 2026-11-14 with xunit 2 (4 tests run, 3 passed, 1 failed, each at its line of the
`.vb` file). They are found by the test runner once the project has been built (Run or Discover), not read from the
source before, as C#'s are.

**Debugging (stage 4).** Visual Basic code debugs as C# does: breakpoints, stepping, frames and variables (shown in
C#'s words: `this`, `null`, `int[]`). In a frame of Visual Basic code an expression is read as Visual Basic, in
Watches, the evaluate box, a hover and a breakpoint's condition: `rows(1)`, `Me.Name`, `label Is Nothing`,
`open AndAlso total > 3`, `total = 6` (a comparison), `names(0) & "!"`, `If(label, "none")`, `CType(total, Long)`,
`TypeOf x Is T`, `New List(Of Integer) From {1, 2}`, `$"{Name}: {total:D3}"`. A C# expression still evaluates there.

**Visual Basic projects are projects.** A folder is walked for `.vbproj` as for `.csproj`, so a Visual Basic project
opened by its folder is in the Solution view, has its run configuration, and builds and runs through MSBuild as a C#
project does. Before, only a solution that named a `.vbproj` knew it; a folder holding one showed "No
configurations". Roslyn's C# server is never given a Visual Basic project.

## How it is built

| Part | Where | Why so |
|---|---|---|
| The language | `Language.VisualBasic` in `src/Passenger.Languages/Language.cs` | one entry, as every TextMate language is |
| The grammar | `src/Passenger.Languages/TextMate/Grammars/visualbasic.tmLanguage.json` | Passenger's own: see below |
| A Markdown block | `EmbeddedAs` in `Language.cs`, `meta.embedded.block.vs_net` in `ScopeColours.cs` | VS Code's Markdown grammar names Visual Basic `source.asp.vb.net` |
| The apostrophe | `TextEditing.Type`'s `comment`, given the editor's `LineCommentPrefix` | the editor names no language |
| The folds | `VisualBasicFolds` behind `FoldScan.VisualBasic` (`src/Passenger.Languages/Markup`) | a scan of lines off the UI thread, as XML's and JSON's: the editor's process carries no Visual Basic parser |
| Block ends | `VisualBasicBlocks` (`src/Passenger.Text`), `Language.Blocks`, `CodeEditor.Blocks`, `TextEditing.NewLine`'s `blocks` | the editor names no language; the line reading is shared with the folds |
| The language server | `src/Passenger.VisualBasic`, carried under `visualbasic/` beside the app | a process of its own on Passenger's runtime (`ChildProcesses.HelperCommand`), as the debug adapter is |
| Its place in the App | `WebServerKind.VisualBasic` in `Web/WebLanguages.cs` | it speaks the standard protocol, so everything the web languages' servers give a file (`WebDocument`, `WebAssist`) it gives a `.vb` file |
| Its projects | `ProjectSession.VisualBasicBuildsAsync`, `WebLanguages.VisualBasicProjects` | the project host's design-time builds, which already read `VbcCommandLineArgs` |
| The debugger's expressions | `VisualBasicExpressions` in `src/Passenger.Debugger`, asked by `CilExpressionCompiler.TryCompile` | Roslyn publishes no expression compiler for Visual Basic, and nuget.org's is C#'s alone |
| The tests | `FileColoursTests` (a file's kinds, its comment, the apostrophe, Markdown), `EditingTests` (typing), `FoldingTests` (the folds), `VisualBasicBlocksTests` (Enter), `VisualBasicExpressionsTests` (the debugger's reading), `VisualBasicTests` (the server and the templates through the window) | |

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

**How a block's end is decided.** The line is read as the folds read it. Its end is written where the file has fewer
ends of that kind than lines that open one, counted over the whole file, so a block typed in a balanced file always
gets its end and one that has it never gets a second. An interface's members, a `MustOverride` member, a `Declare`
and a property (which may be an auto-property) open nothing; a property's `Get` and `Set` do.

## The language server

**Why a server of Passenger's own.** Passenger's C# help is Roslyn's language server (`roslyn-language-server.<rid>`,
`RoslynServerVersion` in `Directory.Packages.props`). The package at 5.13.0-1.26514.10 holds
`Microsoft.CodeAnalysis.CSharp*.dll` and no `Microsoft.CodeAnalysis.VisualBasic*.dll`. Visual Basic's Features and
Workspaces assemblies reach into Roslyn's internals, so they must be of the server's very build, and nuget.org, the only
source Passenger restores from, has them at no build a server package matches. The .NET team's own feed (`dotnet-tools`
on Azure DevOps) does carry them at the server's build; that was looked at and set aside, as it breaks the rule that
packages come from nuget.org alone and ties Visual Basic to every move of the server's version, for a language
Microsoft no longer evolves.

**Which Roslyn.** Every stable `Microsoft.CodeAnalysis.VisualBasic.Features` on nuget.org from 4.0.0 to 5.12.0 was
looked at (2026-11-14):

| Version | Built for | What decides |
|---|---|---|
| 4.0.0 to 4.8.0 | net6.0, net7.0, netstandard2.0 | older than the Roslyn Passenger compiles with; a second Roslyn beside the app's |
| 4.9.2 to 4.14.0 | net8.0, net9.0, netstandard2.0 | the same |
| 5.0.0, 5.3.0, 5.6.0 | net8.0 to net10.0 | the same |
| **5.9.0** | net10.0, netstandard2.0 | **the version `Microsoft.CodeAnalysis.CSharp` is pinned at**: one Roslyn in the lock files and the notices |
| 5.12.0 | net10.0, netstandard2.0 | newer than the pin, which Passenger's analyzers hold to the SDK's compiler |

Each version names every other Roslyn package by exact version, and package versions are central
(`Directory.Packages.props`), so the choice is the pin's: 5.9.0. Little is lost by it: Visual Basic's language has had
no new version since 16.9 (2021), so a newer Roslyn brings its compiler fixes and its reading of newer .NET's
metadata, not language. When the C# pin moves, this one moves with it.

**How it works.** The server is a small program over Roslyn's public workspace API, about 1,000 lines:

- `Wire`: the protocol's framing over the standard streams.
- `Projects`: one `AdhocWorkspace`. A project arrives as the project host's design-time builds (the JSON Roslyn's
  server is given for C#), one a target framework; its `VbcCommandLineArgs` are parsed by
  `VisualBasicCommandLineParser`, so options, imports, root namespace, defines and references are the build's own. An
  open file's text is the client's, another's the disk's. A `.vb` file of no project is read against the runtime's own
  assemblies, so it still has errors of syntax and completion.
- `Server`: the loop. A notification is acted on in turn; a request is answered off the loop from the solution as it
  stood when it came, and `$/cancelRequest` cancels it. Diagnostics are pushed 300 ms after typing rests, naming the
  text's version.
- `Answers`, `Outline`: completion (`CompletionService`), hover (`QuickInfoService`), declarations and usages
  (`SymbolFinder`), rename (`Renamer`), formatting (`Formatter`), a file's symbols from its syntax.
- `Signatures`: a call's signatures from the semantic model (a method group's overloads, a type's constructors, an
  indexed property, a delegate's call), as Roslyn's own signature help is not public.

The App starts it when a `.vb` file opens, gives it the folder's `.vbproj` builds before its files (`passenger/projects`)
and again as the workspace, a `.vb` file or a project's inputs change, and tells it of files changed on disk. It runs
with `ConservingMemory`, as Roslyn's server does, and ends with the IDE.

## What does not work yet

- **Semantic colours, code actions and quick fixes, inlay hints**: Roslyn's services for them are internal or need
  more than one request's worth of work; colours stay the grammar's.
- **Analyzers**: only the compiler's diagnostics are said; the SDK's analyzers a project names are not run.
- **A declaration in a referenced assembly**: Go to Declaration answers for source alone.
- **A Visual Basic project that references a C# project** reads it by the assembly it last built, so a change there
  shows after a build; Rename and Find Usages do not cross the two languages.
- **A solution mixing C# and Visual Basic** is untested. Roslyn's server is told of no `.vbproj`, but the C# service
  still counts it among the workspace's projects.
- **A property's whole body** is not written for you (`End Property` with its `Get` and `Set`), and Enter inside a
  block's first line (not at its end) only carries the indentation.
- **Tests before a first build** are not listed: the Tests window's reading of source is C#'s alone, so a Visual Basic
  project's tests come with its first run or Discover. NUnit and MSTest in Visual Basic are untested.
- **The debugger's expressions** are Visual Basic said in C#, not compiled as Visual Basic, so a few differ: `/` on two
  integers divides as C# does (2, not 2.5), `Not` on an integer is no bitwise complement, `=` on two objects compares
  references, and a query, a lambda, `Like`, a date literal and `AddressOf` have no reading. Values are shown in C#'s
  words. A `DebuggerDisplay` of a Visual Basic type is read as C#. No test debugs a Visual Basic program yet: the
  expressions were checked by hand at a breakpoint (21 of them, 2026-11-14) and their reading by unit tests.

## The stages

1. **The language** (done): the entry, the grammar, comments, typing.
2. **Language help** (done, with the gaps above): Passenger's own server on Roslyn 5.9.0.
3. **The editor's own help**: folding, block ends and the type templates (done); tests are found by the runner.
4. **The debugger** (done, with the gaps above): a Visual Basic expression read in C# for the expression compiler.

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
bash dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir ~/.dotnet11   # for the tests' web apps
export DOTNET_ROOT=~/.dotnet11 PATH=~/.dotnet11:$PATH                              # in this repository's shell only
```

The second line matters: `Passenger.Host`, the Roslyn extension and the tests' debuggees are `net10.0` and need a
.NET 10 runtime in the same folder as the SDK that runs them. On Arch and CachyOS the system's `dotnet-sdk` (10) and
the AUR's `dotnet-sdk-bin` (11) conflict, which is why the SDK is not installed through pacman; once `extra` carries
.NET 11, `dotnet-sdk` becomes 11 with `dotnet-sdk-10.0` beside it, and `~/.dotnet11` can be deleted.

## How the debugger reads an expression

The expression compiler Passenger carries is Roslyn's C# one, packed from Roslyn's source by a third party; Roslyn's
Visual Basic one is published nowhere. So in a frame whose PDB names a Visual Basic document, an expression that
parses as Visual Basic (Roslyn's own parser, `Microsoft.CodeAnalysis.VisualBasic` 5.9.0) is written again in C# and
compiled by the C# compiler against the frame, which is language-neutral below the syntax: the frame's locals,
`Me` as `this`, the module's metadata.

`name(1)` is a call or an index in Visual Basic and only one of them in C#, so an expression has several readings:
every call as a call first, then one as an index, then two (the first three calls with arguments, so eight readings at
most). The first that binds is the answer; where none does, the expression is tried as C# as written, and an error is
the first reading's. Visual Basic is tried first, so `a = b` compares where C# would assign.
