namespace Passenger.Text;

/// <summary>What a line a caret breaks opened: a block, whose body is a level in, with the end to write below it where the
/// block has none yet (<c>End Sub</c>), or null where its end is there already.</summary>
public readonly record struct OpenedBlock(string? End);

/// <summary>A language's reading of a line for Enter: the block the line opens, or null for a line that opens none.</summary>
public delegate OpenedBlock? BlockOpener(TextSnapshot text, int line);

/// <summary>Visual Basic's blocks, read a line at a time with no parser: which block a line opens or ends, by its first
/// word past its attributes and modifiers, for the folds and for Enter. Enter after a line that opens a block indents its
/// body, and writes the block's end where the file has fewer ends of that kind than lines that open one: counted over
/// the whole file, so a block typed inside another of its kind gets its own end. An interface's members, a
/// <c>MustOverride</c> one and a property (which may be an auto-property) open nothing.</summary>
public static class VisualBasicBlocks
{
    /// <summary>How much of a line is read: a block's words stand at its start, and a lambda's past this opens nothing.</summary>
    private const int Read = 512;

    private static readonly string[] Modifiers =
    [
        "Public", "Private", "Protected", "Friend", "Shared", "Shadows", "Overloads", "Overrides", "Overridable", "NotOverridable", "MustOverride",
        "MustInherit", "NotInheritable", "Partial", "ReadOnly", "WriteOnly", "Default", "Async", "Iterator", "Widening", "Narrowing", "Custom",
    ];

    /// <summary>The words that open a block ended by <c>End</c> and the word again, a type's first, then a member's, then a statement's.</summary>
    private static readonly string[] Ended =
    [
        "Namespace", "Class", "Module", "Structure", "Interface", "Enum", "Sub", "Function", "Operator", "Get", "Set",
        "If", "Select", "Try", "With", "Using", "SyncLock", "While",
    ];

    private static readonly string[] Ends = [.. Ended.Select(word => "End " + word)];

    private const int Interface = 4;
    private const int Sub = 6;
    private const int Operator = 8;
    private const int Get = 9;
    private const int Set = 10;
    private const int If = 11;
    private const int Try = 13;
    private const string Next = "Next";
    private const string Loop = "Loop";
    private const string EndRegion = "#End Region";

    /// <summary>The identifier a stretch starts with, or nothing.</summary>
    public static ReadOnlySpan<char> Word(ReadOnlySpan<char> rest)
    {
        int end = 0;
        while (end < rest.Length && (char.IsLetterOrDigit(rest[end]) || rest[end] == '_'))
        {
            end++;
        }
        return rest[..end];
    }

    public static bool IsModifier(ReadOnlySpan<char> word) => IndexOf(Modifiers, word) >= 0;

    /// <summary>A line past the attributes it starts with (<c>&lt;Test&gt; Public Sub …</c>), a string in one read as one.</summary>
    public static ReadOnlySpan<char> PastAttributes(ReadOnlySpan<char> rest)
    {
        while (rest.Length > 0 && rest[0] == '<')
        {
            int nested = 0;
            bool quoted = false;
            int i = 0;
            for (; i < rest.Length; i++)
            {
                char c = rest[i];
                quoted ^= c == '"';
                nested += quoted ? 0 : c == '<' ? 1 : c == '>' ? -1 : 0;
                if (nested == 0)
                {
                    break;
                }
            }
            if (i >= rest.Length)
            {
                return [];
            }
            rest = rest[(i + 1)..].TrimStart();
        }
        return rest;
    }

    /// <summary>A line's code: what stands before its comment, an apostrophe in a string being none.</summary>
    public static ReadOnlySpan<char> Code(ReadOnlySpan<char> rest)
    {
        bool quoted = false;
        for (int i = 0; i < rest.Length; i++)
        {
            quoted ^= rest[i] == '"';
            if (rest[i] == '\'' && !quoted)
            {
                return rest[..i].TrimEnd();
            }
        }
        return rest.TrimEnd();
    }

    /// <summary>The word of a lambda that goes on past its line, <c>Sub</c> or <c>Function</c>, or null: the line's last one
    /// whose parameters end the line, a function's return type after them. One with its expression or statement on the
    /// line ends there.</summary>
    public static string? Lambda(ReadOnlySpan<char> code)
    {
        for (int block = Sub; block < Operator; block++)
        {
            int at = code.LastIndexOf(Ended[block], StringComparison.OrdinalIgnoreCase);
            if (at < 0 || (at > 0 && (char.IsLetterOrDigit(code[at - 1]) || code[at - 1] is '_' or '.')))
            {
                continue;
            }
            ReadOnlySpan<char> after = code[(at + Ended[block].Length)..].TrimStart();
            if (after.Length == 0 || after[0] != '(')
            {
                continue;
            }
            int nested = 0;
            int close = 0;
            for (; close < after.Length; close++)
            {
                nested += after[close] == '(' ? 1 : after[close] == ')' ? -1 : 0;
                if (nested == 0)
                {
                    break;
                }
            }
            if (close >= after.Length)
            {
                continue;
            }
            ReadOnlySpan<char> tail = after[(close + 1)..].TrimStart();
            if (tail.IsEmpty || (block != Sub && Word(tail).Equals("As", StringComparison.OrdinalIgnoreCase)))
            {
                return Ended[block];
            }
        }
        return null;
    }

    /// <summary>The block a line opens, for Enter at its end (<see cref="BlockOpener"/>).</summary>
    public static OpenedBlock? Opened(TextSnapshot text, int line)
    {
        Span<char> buffer = stackalloc char[Read];
        if (Of(Line(text, line, buffer)).Opens is null)
        {
            return null;
        }
        Dictionary<string, int> open = new(StringComparer.Ordinal);
        int interfaces = 0;
        string? opened = null;
        for (int index = 0; index < text.LineCount; index++)
        {
            (string? opens, string? ends) = Of(Line(text, index, buffer));
            bool member = opens is not null && Array.IndexOf(Ends, opens) is >= Sub and <= Set && Lambda(Code(Line(text, index, buffer))) is null;
            if (opens is not null && !(member && interfaces > 0))
            {
                open[opens] = open.GetValueOrDefault(opens) + 1;
                opened = index == line ? opens : opened;
            }
            if (ends is not null)
            {
                open[ends] = open.GetValueOrDefault(ends) - 1;
            }
            interfaces += opens == Ends[Interface] ? 1 : ends == Ends[Interface] ? -1 : 0;
        }
        return opened is null ? null : new OpenedBlock(open[opened] > 0 ? opened : null);
    }

    private static ReadOnlySpan<char> Line(TextSnapshot text, int index, Span<char> buffer)
    {
        LineSpan line = text.GetLine(index);
        Span<char> whole = buffer[..Math.Min(line.Length, Read)];
        text.CopyTo(line.Start, whole);
        return whole;
    }

    /// <summary>What a line opens and what it ends, each by the words that end it (<c>End Sub</c>, <c>Next</c>, <c>Loop</c>,
    /// <c>#End Region</c>), or null.</summary>
    private static (string? Opens, string? Ends) Of(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> rest = Code(line.TrimStart());
        if (rest.IsEmpty)
        {
            return default;
        }
        if (rest[0] == '#')
        {
            ReadOnlySpan<char> directive = rest[1..].TrimStart();
            ReadOnlySpan<char> named = Word(directive);
            return named.Equals("Region", StringComparison.OrdinalIgnoreCase) ? (EndRegion, null)
                : named.Equals("End", StringComparison.OrdinalIgnoreCase) && Word(directive[named.Length..].TrimStart()).Equals("Region", StringComparison.OrdinalIgnoreCase)
                    ? (null, EndRegion)
                : default;
        }
        rest = PastAttributes(rest);
        bool bodiless = false;
        ReadOnlySpan<char> word = Word(rest);
        while (IsModifier(word))
        {
            bodiless |= word.Equals("MustOverride", StringComparison.OrdinalIgnoreCase);
            rest = rest[word.Length..].TrimStart();
            word = Word(rest);
        }
        ReadOnlySpan<char> after = rest[word.Length..].TrimStart();
        if (word.Equals("End", StringComparison.OrdinalIgnoreCase))
        {
            return IndexOf(Ended, Word(after)) is >= 0 and int ended ? (null, Ends[ended]) : default;
        }
        if (word.Equals(Next, StringComparison.OrdinalIgnoreCase) || word.Equals(Loop, StringComparison.OrdinalIgnoreCase))
        {
            return (null, word.Equals(Next, StringComparison.OrdinalIgnoreCase) ? Next : Loop);
        }
        if (word.Equals("Wend", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "End While");
        }
        if (word.Equals("For", StringComparison.OrdinalIgnoreCase) || word.Equals("Do", StringComparison.OrdinalIgnoreCase))
        {
            return (word.Equals("For", StringComparison.OrdinalIgnoreCase) ? Next : Loop, null);
        }
        int block = IndexOf(Ended, word);
        bool opens = block switch
        {
            < 0 => false,
            < Sub => true,
            <= Operator => !bodiless,
            Get or Set => after.IsEmpty || after[0] == '(',
            If => rest.EndsWith("Then", StringComparison.OrdinalIgnoreCase) && !char.IsLetterOrDigit(rest[^5]),
            Try => after.IsEmpty,
            _ => !after.IsEmpty,
        };
        if (opens)
        {
            return (Ends[block], null);
        }
        return block < 0 && Lambda(rest) is { } lambda ? ("End " + lambda, null) : default;
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
