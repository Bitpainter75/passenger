using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Languages.Markup;

/// <summary>What folds in a Visual Basic file, read a line at a time with no parser: a namespace, a type, a member and its
/// accessors from the line that opens it to its <c>End</c>, a lambda over several lines, <c>#Region</c> to
/// <c>#End Region</c>, the <c>Imports</c>, a documentation comment and the comments a file begins with. A block is known
/// by its line's first word past its attributes and modifiers, and an <c>End</c> closes the nearest block of its kind,
/// dropping those opened since that have no end of their own (an auto-property, an interface's member).</summary>
internal static class VisualBasicFolds
{
    /// <summary>How much of a line is read: a block's words stand at its start, and a lambda's past this is not folded.</summary>
    private const int Read = 512;

    /// <summary>The words that open a block with an <c>End</c> of their own, as that <c>End</c> names them.</summary>
    private static readonly string[] Blocks =
    [
        "Namespace", "Class", "Module", "Structure", "Interface", "Enum", "Sub", "Function", "Operator", "Property", "Event",
        "Get", "Set", "AddHandler", "RemoveHandler", "RaiseEvent",
    ];

    private const int Interface = 4;
    private const int Sub = 6;
    private const int Property = 9;
    private const int Event = 10;
    private const int Get = 11;
    private const int Region = 100;

    private static readonly string[] Placeholders = [.. Blocks.Select(block => "...End " + block)];

    private struct Opened
    {
        public int Block;
        public int Line;
        public int LineEnd;
    }

    private enum Run
    {
        None,
        Imports,
        DocComment,
        Header,
    }

    public static void Find(TextSnapshot text, List<FoldRegion> found)
    {
        Opened[] stack = new Opened[16];
        int depth = 0;
        Span<char> buffer = stackalloc char[Read];
        Run run = Run.None;
        int runFirst = 0;
        int runLast = 0;
        bool code = false;
        for (int index = 0; index < text.LineCount; index++)
        {
            LineSpan line = text.GetLine(index);
            Span<char> whole = buffer[..Math.Min(line.Length, Read)];
            text.CopyTo(line.Start, whole);
            ReadOnlySpan<char> rest = ((ReadOnlySpan<char>)whole).TrimStart();
            Run now = rest.StartsWith("'''") ? Run.DocComment
                : rest.StartsWith("'") && !code ? Run.Header
                : VisualBasicBlocks.Word(rest).Equals("Imports", StringComparison.OrdinalIgnoreCase) ? Run.Imports
                : Run.None;
            if (now != run)
            {
                Close(run);
                run = now;
                runFirst = index;
            }
            runLast = index;
            if (rest.IsEmpty || rest[0] == '\'')
            {
                continue;
            }
            code = true;
            if (rest[0] == '#')
            {
                Directive(rest[1..].TrimStart(), line);
                continue;
            }
            Statement(rest, line);
        }
        Close(run);

        void Close(Run ended)
        {
            if (ended != Run.None && runLast > runFirst)
            {
                FoldKind kind = ended == Run.Imports ? FoldKind.Imports : ended == Run.DocComment ? FoldKind.DocComment : FoldKind.FileHeader;
                found.Add(new FoldRegion(text.GetLine(runFirst).End, text.GetLine(runLast).End, "...", Kind: kind));
            }
        }

        void Push(int block, LineSpan line)
        {
            if (depth == stack.Length)
            {
                Array.Resize(ref stack, depth * 2);
            }
            stack[depth++] = new Opened { Block = block, Line = line.Line, LineEnd = line.End };
        }

        void End(int block, LineSpan line)
        {
            for (int k = depth - 1; k >= 0; k--)
            {
                if (stack[k].Block != block)
                {
                    continue;
                }
                if (line.Line > stack[k].Line)
                {
                    FoldKind kind = block == Region ? FoldKind.Region : block >= Sub ? FoldKind.Body : FoldKind.Code;
                    found.Add(new FoldRegion(stack[k].LineEnd, line.End, block == Region ? "..." : Placeholders[block], Kind: kind));
                }
                depth = k;
                return;
            }
        }

        void Directive(ReadOnlySpan<char> rest, LineSpan line)
        {
            ReadOnlySpan<char> word = VisualBasicBlocks.Word(rest);
            if (word.Equals("Region", StringComparison.OrdinalIgnoreCase))
            {
                Push(Region, line);
            }
            else if (word.Equals("End", StringComparison.OrdinalIgnoreCase) && VisualBasicBlocks.Word(rest[word.Length..].TrimStart()).Equals("Region", StringComparison.OrdinalIgnoreCase))
            {
                End(Region, line);
            }
        }

        void Statement(ReadOnlySpan<char> rest, LineSpan line)
        {
            rest = VisualBasicBlocks.PastAttributes(rest);
            bool bodiless = false;
            bool custom = false;
            ReadOnlySpan<char> word = VisualBasicBlocks.Word(rest);
            while (VisualBasicBlocks.IsModifier(word))
            {
                bodiless |= word.Equals("MustOverride", StringComparison.OrdinalIgnoreCase);
                custom |= word.Equals("Custom", StringComparison.OrdinalIgnoreCase);
                rest = rest[word.Length..].TrimStart();
                word = VisualBasicBlocks.Word(rest);
            }
            if (word.Equals("End", StringComparison.OrdinalIgnoreCase))
            {
                int ended = IndexOf(Blocks, VisualBasicBlocks.Word(rest[word.Length..].TrimStart()));
                if (ended >= 0)
                {
                    End(ended, line);
                }
                return;
            }
            int block = IndexOf(Blocks, word);
            int inside = depth > 0 ? stack[depth - 1].Block : -1;
            bool opens = block switch
            {
                < 0 => false,
                < Sub => true,
                >= Sub and <= Property => !bodiless && inside != Interface,
                Event => custom,
                Get or Get + 1 => inside == Property,
                _ => inside == Event,
            };
            if (opens)
            {
                Push(block, line);
            }
            else if (VisualBasicBlocks.Lambda(VisualBasicBlocks.Code(rest)) is { } lambda)
            {
                Push(IndexOf(Blocks, lambda), line);
            }
        }
    }

    private static int IndexOf(string[] words, ReadOnlySpan<char> word)
    {
        for (int i = 0; i < words.Length; i++)
        {
            if (word.Equals(words[i], StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }
}
