using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Agents;
using Passenger.App.Editors;
using Passenger.App.ToolWindows;
using Passenger.Editor;

namespace Passenger.App.Agents;

/// <summary>The Agent window: a tab for each agent Passenger has a client for (Claude Code, Codex), the chosen one's
/// conversation under them, and the box the person writes in at the bottom, Enter sending and Shift+Enter a new line.
/// A message is drawn as the Markdown it is once written to its end; a tool is a row saying how it stands, with the
/// files it changed as diffs and what it wrote behind Output; a question the agent waits on has its answers as buttons
/// in its place. It follows the transcript down while its end shows, and stays where the person scrolled it otherwise.</summary>
public sealed class AgentView : DockPanel
{
    private readonly Func<AgentChat, string?> whyNot;
    private readonly Action<string> request;
    private readonly Action<Uri> browse;
    private readonly Action<string> open;
    private readonly Func<string?> folder;
    private readonly Dictionary<AgentChat, ToggleButton> tabs = [];
    private readonly StackPanel entries = new() { Name = "AgentEntries", Spacing = 12, Margin = new Thickness(12, 10) };
    private readonly ScrollViewer scroll;
    private readonly EmptyState empty = new();
    private readonly TextBox prompt;
    private readonly Button send;
    private readonly Button stop;
    private readonly Dictionary<AgentEntry, Control> shown = [];

    /// <summary>The tools whose output the person opened.</summary>
    private readonly HashSet<ToolEntry> opened = [];

    private AgentTranscript? following;
    private bool atEnd = true;

    public AgentView(IReadOnlyList<AgentChat> chats, AgentChat chosen, Func<AgentChat, string?> whyNot, Func<AgentChat, string> actionOf,
        Action<string> request, Action<Uri> browse, Action<string> open, Func<string?> folder)
    {
        this.folder = folder;
        this.whyNot = whyNot;
        this.request = request;
        this.browse = browse;
        this.open = open;
        Chat = chosen;
        StackPanel strip = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (AgentChat chat in chats)
        {
            ToggleButton tab = ToolTabs.Tab(chat.Program.Name, chat == chosen);
            tab.Name = $"agent{chat.Program.Kind}Tab";
            string action = actionOf(chat);
            ActionButtons.Mark(tab, action);
            tab.Click += (_, _) =>
            {
                // The action chooses it; a click on the open tab leaves it open.
                tab.IsChecked = chat == Chat;
                request(action);
            };
            tabs[chat] = tab;
            strip.Children.Add(tab);
            chat.Renewed += (_, _) =>
            {
                if (chat == Chat)
                {
                    Follow();
                }
            };
        }
        StackPanel tools = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 6, 0),
            [DockProperty] = Dock.Right,
            Children = { ToolButtons.Of(AppActions.AgentNew, IconKeys.Add, "New Conversation", request) },
        };
        Children.Add(ToolBars.Top(new DockPanel { Children = { tools, strip } }));

        prompt = new TextBox
        {
            Name = "AgentPrompt",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 140,
            VerticalAlignment = VerticalAlignment.Center,
        };
        prompt.AddHandler(KeyDownEvent, OnPromptKey, RoutingStrategies.Tunnel);
        send = ToolButtons.Of(AppActions.AgentSend, IconKeys.Send, "Send", request);
        stop = ToolButtons.Of(AppActions.AgentStop, IconKeys.Stop, "Stop", request);
        Panel buttons = new() { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, [DockProperty] = Dock.Right, Children = { send, stop } };
        Border box = new()
        {
            [DockProperty] = Dock.Bottom,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new DockPanel { Margin = new Thickness(8, 6), Children = { buttons, prompt } },
        };
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable(TokenKeys.Divider));
        Children.Add(box);

        scroll = new ScrollViewer { Content = entries, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.ScrollChanged += (_, e) =>
        {
            // The person's own scroll says whether it follows; the transcript growing does not.
            if (e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0)
            {
                atEnd = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 4;
            }
        };
        entries.SizeChanged += (_, _) =>
        {
            if (atEnd)
            {
                scroll.ScrollToEnd();
            }
        };
        Children.Add(new Panel { Children = { scroll, empty } });
        Follow();
    }

    /// <summary>The conversation shown.</summary>
    public AgentChat Chat { get; private set; }

    /// <summary>What is written in the box.</summary>
    public string Written
    {
        get => prompt.Text ?? string.Empty;
        set => prompt.Text = value;
    }

    /// <summary>What the transcript shows, an entry a line: who says it, then its words.</summary>
    public IReadOnlyList<string> Shown => [.. entries.Children.Select(c => c.Tag as string ?? string.Empty)];

    /// <summary>What the window says in place of a conversation, or null while it shows one.</summary>
    public string? EmptyWords => empty.IsVisible ? empty.Text : null;

    /// <summary>The buttons of the question the agent waits on, by their words; none while it asks nothing.</summary>
    public IReadOnlyList<Button> Answers =>
        [.. entries.Children.OfType<Border>().Where(b => b.Tag is string asked && asked.StartsWith("Asks", StringComparison.Ordinal))
            .SelectMany(b => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(b).OfType<Button>())];

    /// <summary>Shows another agent's conversation.</summary>
    public void Show(AgentChat chat)
    {
        Chat = chat;
        Follow();
    }

    /// <summary>Draws what stands outside the transcript again: the tabs, Send or Stop, the box's and the empty words.</summary>
    public void Refresh()
    {
        foreach ((AgentChat chat, ToggleButton tab) in tabs)
        {
            tab.IsChecked = chat == Chat;
        }
        AgentTranscript transcript = Chat.Transcript;
        stop.IsVisible = transcript.Working;
        send.IsVisible = !transcript.Working;
        prompt.PlaceholderText = transcript.Model is { Length: > 0 } model ? $"Ask {Chat.Program.Name} ({model})" : $"Ask {Chat.Program.Name}";
        string? refused = whyNot(Chat);
        empty.Text = refused ?? $"Ask {Chat.Program.Name} about this folder, or to change it. It works here as it does in a terminal, signed in as you signed it in.";
        empty.IsVisible = transcript.Entries.Count == 0;
        scroll.IsVisible = !empty.IsVisible;
    }

    private void Follow()
    {
        if (following is { } before)
        {
            before.Added -= Add;
            before.Changed -= Change;
            before.StateChanged -= Refresh;
        }
        AgentTranscript transcript = following = Chat.Transcript;
        transcript.Added += Add;
        transcript.Changed += Change;
        transcript.StateChanged += Refresh;
        entries.Children.Clear();
        shown.Clear();
        opened.Clear();
        atEnd = true;
        foreach (AgentEntry entry in transcript.Entries)
        {
            Add(entry);
        }
        Refresh();
    }

    private void Add(AgentEntry entry)
    {
        entries.Children.Add(shown[entry] = Made(entry));
        Refresh();
    }

    private void Change(AgentEntry entry)
    {
        if (!shown.TryGetValue(entry, out Control? drawn))
        {
            return;
        }
        // A message still being written grows in the text it has, a key's worth of work a delta.
        if (entry is WordsEntry { Done: false } words && drawn is SelectableTextBlock writing)
        {
            writing.Text = words.Text;
            writing.Tag = Said(words);
            return;
        }
        entries.Children[entries.Children.IndexOf(drawn)] = shown[entry] = Made(entry);
    }

    private Control Made(AgentEntry entry) => entry switch
    {
        PersonEntry person => new Border
        {
            Classes = { StyleClasses.Framed },
            Padding = new Thickness(10, 6),
            Tag = $"You: {person.Text}",
            Child = new SelectableTextBlock { Text = person.Text, TextWrapping = TextWrapping.Wrap },
        },
        WordsEntry { Done: false } words => new SelectableTextBlock { Text = words.Text, TextWrapping = TextWrapping.Wrap, Tag = Said(words) },
        WordsEntry words => Message(words),
        ToolEntry tool => Tool(tool),
        AskEntry ask => Ask(ask),
        NoticeEntry notice => Notice(notice),
        _ => new Panel(),
    };

    private string Said(WordsEntry words) => $"{Chat.Program.Name}: {words.Text}";

    private StackPanel Message(WordsEntry words)
    {
        StackPanel page = new() { Tag = Said(words) };
        Markdown.Render(Markdown.Parse(words.Text), page, browse);
        return page;
    }

    private StackPanel Tool(ToolEntry tool)
    {
        RowIcon state = new()
        {
            Width = 12,
            Height = 12,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            DataKey = tool.State switch { ToolState.Running => IconKeys.TestRunning, ToolState.Done => IconKeys.TestPassed, _ => IconKeys.TestFailed },
            ForegroundKey = tool.State switch { ToolState.Running => TokenKeys.Test_Running, ToolState.Done => TokenKeys.Test_Passed, _ => TokenKeys.Test_Failed },
        };
        TextBlock title = new() { Text = tool.Title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        string first = tool.Detail.Split('\n')[0];
        TextBlock detail = MutedText.Of(first).InCodeFont();
        detail.FontSize = 12;
        ToolTip.SetTip(detail, tool.Detail.Length > first.Length ? tool.Detail : null);
        DockPanel head = new() { Children = { state, title } };
        StackPanel row = new() { Spacing = 4, Tag = $"{tool.Title} ({tool.State}): {tool.Detail}" };
        row.Children.Add(head);
        // Its files are named under it, so the row itself need not say them twice.
        if (tool.Files.Count == 0)
        {
            head.Children.Add(detail);
        }
        foreach (AgentFileEdit file in tool.Files)
        {
            row.Children.Add(File(file));
        }
        if (tool.Output is { } output)
        {
            Border wrote = CodeBlock.Of(output);
            wrote.MaxHeight = 220;
            wrote.Margin = new Thickness(20, 0, 0, 0);
            Control text = wrote.Child!;
            wrote.Child = null;
            wrote.Child = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            wrote.IsVisible = tool.State == ToolState.Failed || opened.Contains(tool);
            Button toggle = new() { Classes = { StyleClasses.Link }, Content = "Output", FontSize = 12, Margin = new Thickness(8, 0, 0, 0), [DockProperty] = Dock.Right };
            toggle.Click += (_, _) =>
            {
                wrote.IsVisible = !wrote.IsVisible;
                if (!wrote.IsVisible)
                {
                    opened.Remove(tool);
                }
                else
                {
                    opened.Add(tool);
                }
            };
            head.Children.Insert(0, toggle);
            row.Children.Add(wrote);
        }
        return row;
    }

    /// <summary>A changed file: its path, which opens it, over its diff's lines, each in the colour of what it did.</summary>
    private StackPanel File(AgentFileEdit file)
    {
        Button path = new()
        {
            Classes = { StyleClasses.Link },
            Content = $"{file.Change switch { AgentFileChange.Added => "Added", AgentFileChange.Deleted => "Deleted", _ => "Changed" }} {Passenger.Core.PathNames.Shown(file.Path, folder())}",
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12,
            IsEnabled = file.Change != AgentFileChange.Deleted,
        };
        path.Click += (_, _) => open(file.Path);
        StackPanel edit = new() { Spacing = 2, Margin = new Thickness(20, 0, 0, 0), Children = { path } };
        if (file.Diff.Length > 0)
        {
            StackPanel lines = new();
            foreach (string line in file.Diff.Split('\n'))
            {
                TextBlock drawn = new TextBlock { Text = line.Length == 0 ? " " : line, FontSize = 12 }.InCodeFont();
                // A line is on the ground a diff gives what it did, as the diff view draws it.
                if (line.Length > 0 && (line[0] switch { '+' => TokenKeys.Diff_Added, '-' => TokenKeys.Diff_Deleted, _ => null }) is { } ground)
                {
                    drawn.Bind(TextBlock.BackgroundProperty, drawn.GetResourceObservable(ground));
                }
                else if (line.StartsWith('@'))
                {
                    drawn.Classes.Add(StyleClasses.Muted);
                }
                lines.Children.Add(drawn);
            }
            edit.Children.Add(new Border
            {
                Classes = { StyleClasses.Code },
                MaxHeight = 260,
                Child = new ScrollViewer { Content = lines, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto },
            });
        }
        return edit;
    }

    /// <summary>The agent's question: what it wants leave for, and its answers while it waits, then what was answered.</summary>
    private Border Ask(AskEntry ask)
    {
        StackPanel page = new() { Spacing = 6 };
        page.Children.Add(new TextBlock { Text = $"{Chat.Program.Name} asks: {ask.Title}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (ask.Detail.Length > 0)
        {
            Border detail = CodeBlock.Of(ask.Detail);
            detail.MaxHeight = 200;
            page.Children.Add(detail);
        }
        string answered = ask.Answer switch
        {
            AgentAnswer.Allow => "Allowed",
            AgentAnswer.AllowAlways => "Allowed, and not asked again",
            AgentAnswer.Deny => "Not allowed",
            _ => "Not answered",
        };
        if (ask.Open)
        {
            WrapPanel answers = new() { ItemSpacing = 6, LineSpacing = 6 };
            answers.Children.Add(Answer(ask, "Allow", AgentAnswer.Allow));
            if (ask.OffersAlways)
            {
                answers.Children.Add(Answer(ask, "Allow, Don't Ask Again", AgentAnswer.AllowAlways));
            }
            answers.Children.Add(Answer(ask, "Don't Allow", AgentAnswer.Deny));
            page.Children.Add(answers);
        }
        else
        {
            page.Children.Add(MutedText.Of(answered));
        }
        return new Border
        {
            Classes = { StyleClasses.Framed },
            Padding = new Thickness(10, 8),
            Tag = ask.Open ? $"Asks {ask.Title}: {ask.Detail}" : $"Asked {ask.Title} ({answered}): {ask.Detail}",
            Child = page,
        };
    }

    private Button Answer(AskEntry ask, string words, AgentAnswer answer)
    {
        // No default among them: a stray Enter never answers an agent.
        Button button = new() { Content = words, Focusable = false };
        button.Click += (_, _) => Chat.Answer(ask, answer);
        return button;
    }

    private static TextBlock Notice(NoticeEntry notice)
    {
        TextBlock words = MutedText.Wrapped(notice.Text);
        words.Tag = $"{(notice.IsError ? "Error" : "Note")}: {notice.Text}";
        if (notice.IsError)
        {
            words.Classes.Remove(StyleClasses.Muted);
            words.Classes.Add(StyleClasses.Error);
        }
        return words;
    }

    private void OnPromptKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            request(AppActions.AgentSend);
        }
    }
}
