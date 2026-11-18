namespace Presenter.Core;

/// <summary>What a show key means. Enter is <see cref="Next"/>, except after
/// digits, where it jumps to that slide.</summary>
public enum ShowAction { Next, Prev, First, Last, Black, White, End, Digit }

/// <summary>A key press the hook took for the show.</summary>
/// <param name="Timestamp">When the hook saw it, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
public readonly record struct KeyPress(ShowAction Action, int VirtualKey, long Timestamp, int Digit = -1)
{
    public bool IsEnter => VirtualKey == KeyMap.VkEnter;
}

/// <summary>The show keys (E10): a keyboard and the usual presenter remotes'
/// codes (Page Up/Down, B and period for black).</summary>
public static class KeyMap
{
    public const int VkEnter = 0x0D, VkEscape = 0x1B;

    public static ShowAction? Action(int vk) => vk switch
    {
        0x27 or 0x28 or 0x20 or VkEnter or 0x22 or 0x4E => ShowAction.Next,  // Right Down Space Enter PageDown N
        0x25 or 0x26 or 0x08 or 0x21 or 0x50 => ShowAction.Prev,             // Left Up Backspace PageUp P
        0x24 => ShowAction.First,                                              // Home
        0x23 => ShowAction.Last,                                               // End
        0x42 or 0xBE => ShowAction.Black,                                      // B .
        0x57 or 0xBC => ShowAction.White,                                      // W ,
        VkEscape => ShowAction.End,
        >= 0x30 and <= 0x39 => ShowAction.Digit,                               // 0-9 (top row)
        >= 0x60 and <= 0x69 => ShowAction.Digit,                               // numpad 0-9
        _ => null,
    };

    public static KeyPress? Press(int vk, long timestamp) => Action(vk) is { } a
        ? new KeyPress(a, vk, timestamp, a == ShowAction.Digit ? (vk >= 0x60 ? vk - 0x60 : vk - 0x30) : -1)
        : null;
}
