using System.Globalization;
using Presenter.Cli;

// The machine's culture may write 0,5 for 0.5 (E20): keep output invariant.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const string Usage = """
    presenter: present a slide deck live in Microsoft Paint.

      presenter present <deck> [--monitor N] [--no-painting]
          Show a deck in Paint on the chosen display (default: the primary).
          <deck> is a folder of slide images, for now.
          Keys: Right/Space/PageDown next, Left/PageUp back, Home the loading
          painting, End the last slide, digits + Enter jump, B/W black/white
          screen, Esc ends.

      presenter screens
          List the displays and the slide size each would get.

      presenter pick
          Show the screen picker and print the choice.

      presenter doctor [--monitor N] [--json]
          Check this machine: displays, Paint and the elements the presenter
          drives, a pixel-exact test paste, paste speed, the keyboard hook,
          the renderers' prerequisites. Opens Paint for about half a minute.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 1 : 0;
}

try
{
    return args[0] switch
    {
        "present" => await PresentCommand.Run(args[1..]),
        "screens" => ScreensCommand.Run(),
        "doctor" => await DoctorCommand.Run(args[1..]),
        "pick" => await PickCommand.Run(args[1..]),
        _ => throw new UsageException($"unknown command: {args[0]}"),
    };
}
catch (UsageException e)
{
    Console.Error.WriteLine($"presenter: {e.Message}\n\n{Usage}");
    return 2;
}

internal sealed class UsageException(string message) : Exception(message);
