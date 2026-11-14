using System.Runtime.InteropServices;
using KohUI.Backends.Sdl3;
using KohUI.Theme;
using SDL3;

namespace KohUI.Sdl3.Tests;

[NotInParallel]
public class InputTests
{
    private sealed record Model(
        int Clicks = 0,
        string Text = "",
        int Downs = 0,
        int Ups = 0,
        int Closes = 0,
        int Edits = 0
    );

    private sealed record Message(string Kind, string Text = "");

    private sealed record View(Model Model) : IView<Message>
    {
        public RenderNode Render() =>
            RenderNode.WithChildren(
                "Window",
                [
                    RenderNode.Leaf(
                        "Button",
                        Props.Of(("text", "&OK"), ("onClick", (Func<Message>)(() => new("click"))))
                    ),
                    RenderNode.WithChildren(
                        "Panel",
                        [
                            RenderNode.Leaf(
                                "TextBox",
                                Props.Of(
                                    ("text", Model.Text),
                                    ("onChange", (Func<string, Message>)(text => new("text", text)))
                                )
                            ),
                        ]
                    ),
                    RenderNode.Leaf(
                        "MenuItem",
                        Props.Of(
                            ("text", "&Open"),
                            ("onClick", (Func<Message>)(() => new("click")))
                        )
                    ),
                ],
                Props.Of(("onClose", (Func<Message>)(() => new("close"))))
            );
    }

    [Test]
    public void Native_Events_Dispatch_And_Cancellation_Cleans_Up()
    {
        if (Environment.GetEnvironmentVariable("KOH_SDL_WINDOW_TEST") != "1")
            Skip.Test("Set KOH_SDL_WINDOW_TEST=1 to exercise native input");
        var runner = new Runner<Model, Message>(
            new(),
            (message, model) =>
                message.Kind switch
                {
                    "click" => model with { Clicks = model.Clicks + 1 },
                    "text" => model with
                    {
                        Text = message.Text.Contains('x') ? model.Text : message.Text,
                        Edits = model.Edits + 1,
                    },
                    "down" => model with { Downs = model.Downs + 1 },
                    "up" => model with { Ups = model.Ups + 1 },
                    "close" => model with { Closes = model.Closes + 1 },
                    _ => model,
                },
            model => new View(model)
        );
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        nint first = Marshal.StringToCoTaskMemUTF8("aé");
        nint second = Marshal.StringToCoTaskMemUTF8("b");
        nint rejected = Marshal.StringToCoTaskMemUTF8("x");
        nint accepted = Marshal.StringToCoTaskMemUTF8("c");
        int stage = 0;
        try
        {
            var backend = new Sdl3Backend<Model, Message>(
                runner,
                width: 160,
                height: 144,
                onTick: () =>
                {
                    var windows = SDL.GetWindows(out int count);
                    Require(count == 1 && windows is not null, "one native window");
                    uint id = SDL.GetWindowID(windows![0]);
                    var model = runner.CurrentModel;
                    switch (stage)
                    {
                        case 0:
                            var layout = new Layouter(Win98Theme.Default).Layout(
                                runner.CurrentRender!,
                                160,
                                144
                            );
                            var button = layout.Children[0].Bounds;
                            Push(
                                new SDL.Event
                                {
                                    Button = new SDL.MouseButtonEvent
                                    {
                                        Type = SDL.EventType.MouseButtonDown,
                                        WindowID = id,
                                        Button = SDL.ButtonLeft,
                                        X = button.X + button.W / 2,
                                        Y = button.Y + button.H / 2,
                                    },
                                }
                            );
                            Push(
                                new SDL.Event
                                {
                                    Button = new SDL.MouseButtonEvent
                                    {
                                        Type = SDL.EventType.MouseButtonUp,
                                        WindowID = id,
                                        Button = SDL.ButtonLeft,
                                        X = button.X + button.W / 2,
                                        Y = button.Y + button.H / 2,
                                    },
                                }
                            );
                            stage++;
                            break;
                        case 1 when model.Clicks == 1:
                            Key(id, SDL.Scancode.Tab);
                            Push(
                                new SDL.Event
                                {
                                    Text = new SDL.TextInputEvent
                                    {
                                        Type = SDL.EventType.TextInput,
                                        WindowID = id,
                                        Text = first,
                                    },
                                }
                            );
                            Push(
                                new SDL.Event
                                {
                                    Text = new SDL.TextInputEvent
                                    {
                                        Type = SDL.EventType.TextInput,
                                        WindowID = id,
                                        Text = second,
                                    },
                                }
                            );
                            stage++;
                            break;
                        case 2 when model.Text == "ab":
                            Key(id, SDL.Scancode.Backspace);
                            stage++;
                            break;
                        case 3 when model.Text == "a":
                            Key(id, SDL.Scancode.Tab, SDL.Keymod.Shift);
                            Key(id, SDL.Scancode.Return);
                            stage++;
                            break;
                        case 4 when model.Clicks == 2:
                            Key(id, SDL.Scancode.O, SDL.Keymod.Alt);
                            stage++;
                            break;
                        case 5 when model.Clicks == 3:
                            Key(id, SDL.Scancode.Z);
                            Push(
                                new SDL.Event
                                {
                                    Key = new SDL.KeyboardEvent
                                    {
                                        Type = SDL.EventType.KeyUp,
                                        WindowID = id,
                                        Scancode = SDL.Scancode.Z,
                                    },
                                }
                            );
                            stage++;
                            break;
                        case 6 when model.Downs == 1 && model.Ups == 1:
                            Key(id, SDL.Scancode.Z);
                            Push(
                                new SDL.Event
                                {
                                    Window = new SDL.WindowEvent
                                    {
                                        Type = SDL.EventType.WindowFocusLost,
                                        WindowID = id,
                                    },
                                }
                            );
                            stage++;
                            break;
                        case 7 when model.Downs == 2 && model.Ups == 2:
                            Key(id, SDL.Scancode.Tab);
                            Key(id, SDL.Scancode.Tab);
                            Push(
                                new SDL.Event
                                {
                                    Text = new SDL.TextInputEvent
                                    {
                                        Type = SDL.EventType.TextInput,
                                        WindowID = id,
                                        Text = rejected,
                                    },
                                }
                            );
                            stage++;
                            break;
                        case 8 when model.Edits == 4 && model.Text == "a":
                            Push(
                                new SDL.Event
                                {
                                    Text = new SDL.TextInputEvent
                                    {
                                        Type = SDL.EventType.TextInput,
                                        WindowID = id,
                                        Text = accepted,
                                    },
                                }
                            );
                            stage++;
                            break;
                        case 9 when model.Text == "ac":
                            Push(
                                new SDL.Event
                                {
                                    Window = new SDL.WindowEvent
                                    {
                                        Type = SDL.EventType.WindowCloseRequested,
                                        WindowID = id,
                                    },
                                }
                            );
                            stage++;
                            break;
                        case 10 when model.Closes == 1:
                            stage++;
                            stop.Cancel();
                            break;
                    }
                    return new Message("tick");
                },
                onKeyDown: key => key == "KeyZ" ? new Message("down") : null!,
                onKeyUp: key => key == "KeyZ" ? new Message("up") : null!
            );
            backend.Run(stop.Token);
            Require(stage == 11, $"input sequence stage {stage}, model {runner.CurrentModel}");
            Require(
                (SDL.WasInit(SDL.InitFlags.Video) & SDL.InitFlags.Video) == 0,
                "video subsystem cleanup"
            );
        }
        finally
        {
            Marshal.FreeCoTaskMem(first);
            Marshal.FreeCoTaskMem(second);
            Marshal.FreeCoTaskMem(rejected);
            Marshal.FreeCoTaskMem(accepted);
            runner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void Key(uint id, SDL.Scancode code, SDL.Keymod mod = 0) =>
        Push(
            new SDL.Event
            {
                Key = new SDL.KeyboardEvent
                {
                    Type = SDL.EventType.KeyDown,
                    WindowID = id,
                    Scancode = code,
                    Mod = mod,
                },
            }
        );

    private static void Push(SDL.Event input) =>
        Require(SDL.PushEvent(ref input), "SDL event enqueue");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"{message}: {SDL.GetError()}");
    }
}
