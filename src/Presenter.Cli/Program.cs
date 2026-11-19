using System.Globalization;
using Presenter.Cli;

// The machine's culture may write 0,5 for 0.5 (E20): keep output invariant.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const string Usage = """
    presenter: present a slide deck live in Microsoft Paint.

      presenter
          Demo mode: the built-in help deck (how to use the presenter).

      presenter present <deck> [--monitor N] [--no-painting] [--title "…"]
          Show a deck in Paint. <deck>: a PowerPoint file (.pptx, .ppt), a
          PDF, a Marp Markdown file, a Slidev project, or a folder of slide
          images; rendered by the deck's own tool at the size the screen
          needs, through a cache. The screen picker asks where first (with
          the pre-flight reminders); --monitor N goes straight to display N.
          Keys: Right/Space/PageDown next, Left/PageUp back, Home the loading
          painting, End the last slide, digits + Enter jump, B/W black/white
          screen, Esc ends.

      presenter render <deck> [--size WxH | --monitor N] [--out folder]
          Render a deck's slides without Paint (warms the cache for a show).

      presenter setup
          Install Marp's command line for Marp decks (needs Node and npm).

      presenter screens
          List the displays and the slide size each would get.

      presenter pick
          Show the screen picker and print the choice.

      presenter doctor [--monitor N] [--json]
          Check this machine: displays, Paint and the elements the presenter
          drives, a pixel-exact test paste, paste speed, the keyboard hook,
          the renderers' prerequisites. Opens Paint for about half a minute.
    """;

if (args.Length > 0 && args[0] is "--version" or "version")
{
    Console.WriteLine(typeof(UsageException).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?");
    return 0;
}

if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

// no command (or only options): demo mode, the built-in help deck
if (args.Length == 0 || args[0].StartsWith("--") || args[0] == "demo")
    args = ["present", .. args.SkipWhile(a => a == "demo")];

try
{
    return args[0] switch
    {
        "present" => await PresentCommand.Run(args[1..]),
        "screens" => ScreensCommand.Run(),
        "doctor" => await DoctorCommand.Run(args[1..]),
        "pick" => await PickCommand.Run(args[1..]),
        "render" => await RenderCommand.Run(args[1..]),
        "setup" => SetupCommand.Run(args[1..]),
        _ => throw new UsageException($"unknown command: {args[0]}"),
    };
}
catch (UsageException e)
{
    Console.Error.WriteLine($"presenter: {e.Message}\n\n{Usage}");
    return 2;
}

internal sealed class UsageException(string message) : Exception(message);
