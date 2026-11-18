using System.Diagnostics;
using System.Threading.Channels;
using Presenter.Core;
using static Presenter.Windows.Native;

namespace Presenter.Windows;

/// <summary>Synthetic keys. The presenter's own keys carry <see cref="OwnTag"/>
/// in <c>dwExtraInfo</c>, so its own hook lets them through (E10).</summary>
public static unsafe class Keyboard
{
    /// <summary>"PANT": marks a key as the presenter's own.</summary>
    public const uint OwnTag = 0x50414E54;
    public const int VkControl = 0x11, VkHeartbeat = 0x97;

    /// <summary>Press and release keys in order, tagged as the presenter's own.
    /// Each (vk, up) pair is one event.</summary>
    public static void Send(ReadOnlySpan<(int Vk, bool Up)> events, bool tagged = true)
    {
        INPUT* inputs = stackalloc INPUT[events.Length];
        for (int i = 0; i < events.Length; i++)
            inputs[i] = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)events[i].Vk,
                    dwFlags = events[i].Up ? KEYEVENTF_KEYUP : 0,
                    dwExtraInfo = tagged ? OwnTag : 0,
                },
            };
        SendInput((uint)events.Length, inputs, sizeof(INPUT));
    }

    /// <summary>One key, down and up.</summary>
    public static void Tap(int vk, bool tagged = true) => Send([(vk, false), (vk, true)], tagged);

    /// <summary>Ctrl + a key, tagged.</summary>
    public static void Ctrl(int vk) => Send([(VkControl, false), (vk, false), (vk, true), (VkControl, true)]);

    /// <summary>Text as Unicode key events (VK_PACKET), one SendInput call:
    /// what a text box needs (E16), and not a show key for the hook.</summary>
    public static void Type(string text)
    {
        INPUT* inputs = stackalloc INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
            for (int up = 0; up < 2; up++)
                inputs[i * 2 + up] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    ki = new KEYBDINPUT { wScan = text[i], dwFlags = KEYEVENTF_UNICODE | (up == 1 ? KEYEVENTF_KEYUP : 0) },
                };
        SendInput((uint)(text.Length * 2), inputs, sizeof(INPUT));
    }
}

/// <summary>
/// The low-level keyboard hook (E10, E23; research 05 §2). It runs on a
/// thread of its own that does nothing else, and its callback only records
/// the key in a preallocated ring: Windows waits about 300 ms for the hook,
/// then passes the key on and holds up every keyboard in the session. A show
/// key is swallowed only while one of the target windows (Paint) is in
/// front; keys carrying <see cref="Keyboard.OwnTag"/> always pass.
/// </summary>
public sealed class KeyHook : IDisposable
{
    private const int RingSize = 256;
    private const uint WM_APP_REHOOK = 0x8001;
    private readonly HookProc _proc;  // rooted for the hook's lifetime
    private readonly int[] _ringVk = new int[RingSize];
    private readonly long[] _ringTime = new long[RingSize];
    private long _written, _read;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Channel<KeyPress> _keys = Channel.CreateUnbounded<KeyPress>();
    private readonly Thread _hookThread, _pumpThread;
    private readonly ManualResetEventSlim _heartbeat = new(false);
    private readonly ManualResetEventSlim _rehooked = new(false);
    private volatile nint[] _targets = [];
    private volatile bool _stopping;
    private uint _hookThreadId;
    private nint _hook;

    /// <summary>The show keys taken while a target was in front.</summary>
    public ChannelReader<KeyPress> Keys => _keys.Reader;

    /// <summary>Set by the first show key taken (it stays in <see cref="Keys"/>):
    /// the loading painting stops on it.</summary>
    public CancellationTokenSource FirstKey { get; } = new();

    public KeyHook(params nint[] targets)
    {
        _targets = targets;
        _proc = Callback;
        using var ready = new ManualResetEventSlim();
        int error = 0;
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(0), 0);
            if (_hook == 0) error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            ready.Set();
            while (_hook != 0 && GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.message != WM_APP_REHOOK) continue;
                // Windows removes a hook that timed out without telling us (E10)
                UnhookWindowsHookEx(_hook);
                _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(0), 0);
                _rehooked.Set();
            }
            if (_hook != 0) UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "key hook" };
        _hookThread.Start();
        ready.Wait();
        if (_hook == 0) throw new InvalidOperationException($"SetWindowsHookEx failed: {error}");
        _pumpThread = new Thread(Pump) { IsBackground = true, Name = "key queue" };
        _pumpThread.Start();
    }

    /// <summary>The windows whose being in front makes show keys ours.</summary>
    public void SetTargets(params nint[] targets) => _targets = targets;

    /// <summary>
    /// During the show, also swallow the keys that would change Paint under
    /// the speaker's slides while it is in front: Ctrl combinations (Ctrl+Z
    /// would undo the slide, Ctrl+S or Ctrl+P open dialogs), Delete, Insert
    /// and the F keys (F11 changes Paint's view). Alt and Windows-key
    /// combinations still pass, so Alt+Tab works.
    /// </summary>
    public bool GuardPaint { get; set; }

    private static bool ChangesPaint(uint vk)
    {
        if (vk is 0x2E or 0x2D) return true;                 // Delete, Insert
        if (vk is >= 0x70 and <= 0x87) return true;          // F1-F24
        bool modifier = vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;
        return !modifier && (GetAsyncKeyState(0x11) & 0x8000) != 0;  // anything with Ctrl held
    }

    private unsafe nint Callback(int code, nuint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var kb = (KBDLLHOOKSTRUCT*)lParam;
            if (kb->dwExtraInfo == Keyboard.OwnTag)
            {
                if (kb->vkCode == Keyboard.VkHeartbeat)
                {
                    _heartbeat.Set();
                    return 1;
                }
                return CallNextHookEx(0, code, wParam, lParam);
            }
            if (KeyMap.Action((int)kb->vkCode) is not null && IsTargetInFront())
            {
                if (wParam is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    long n = _written;
                    _ringVk[n % RingSize] = (int)kb->vkCode;
                    _ringTime[n % RingSize] = Stopwatch.GetTimestamp();
                    Volatile.Write(ref _written, n + 1);
                    _signal.Set();
                }
                return 1;  // swallowed: Paint never sees it
            }
            if (GuardPaint && ChangesPaint(kb->vkCode) && IsTargetInFront())
                return 1;  // a key that would change Paint under the slides: dropped
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private bool IsTargetInFront()
    {
        nint fg = GetAncestor(GetForegroundWindow(), GA_ROOTOWNER);
        foreach (var t in _targets)
            if (t == fg) return true;
        return false;
    }

    private void Pump()
    {
        while (!_stopping)
        {
            _signal.WaitOne(200);
            long upTo = Volatile.Read(ref _written);
            for (; _read < upTo; _read++)
            {
                if (KeyMap.Press(_ringVk[_read % RingSize], _ringTime[_read % RingSize]) is { } press)
                {
                    _keys.Writer.TryWrite(press);
                    if (!FirstKey.IsCancellationRequested) FirstKey.Cancel();
                }
            }
        }
    }

    /// <summary>Is the hook still called? A tagged key only the hook sees and
    /// swallows (E10); Windows removes a hook that times out silently.</summary>
    public bool Alive(int timeoutMs = 300)
    {
        _heartbeat.Reset();
        Keyboard.Tap(Keyboard.VkHeartbeat);
        return _heartbeat.Wait(timeoutMs);
    }

    /// <summary>Install the hook again (when the heartbeat goes unanswered).
    /// Keys keep arriving in the same <see cref="Keys"/>.</summary>
    public bool Reinstall()
    {
        _rehooked.Reset();
        PostThreadMessageW(_hookThreadId, WM_APP_REHOOK, 0, 0);
        return _rehooked.Wait(1000) && _hook != 0 && Alive();
    }

    public void Dispose()
    {
        _stopping = true;
        PostThreadMessageW(_hookThreadId, WM_QUIT, 0, 0);
        _hookThread.Join(1000);
        _signal.Set();
        _pumpThread.Join(1000);
        _keys.Writer.TryComplete();
        FirstKey.Dispose();
    }
}
