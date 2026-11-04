using System.Runtime.InteropServices;

namespace LingDongDao.Perception;

public static class AgentMouseTool
{
    private const uint InputMouse = 0;
    private const uint LeftDown = 0x0002, LeftUp = 0x0004;
    private const uint RightDown = 0x0008, RightUp = 0x0010;
    private const uint Wheel = 0x0800;

    public static bool IsAllowed(AgentLoopFrame frame, AgentToolDecision action,
        WindowInfo? foreground, out string reason)
    {
        if (action.Kind is not (AgentToolKind.Click or AgentToolKind.RightClick or AgentToolKind.Navigate))
        { reason = "不是鼠标动作"; return false; }
        if (foreground is null || foreground.Handle != frame.Window.Handle ||
            foreground.ProcessId != frame.Window.ProcessId)
        { reason = "目标窗口已失去焦点"; return false; }
        if (!frame.Bounds.Contains(action.X, action.Y) ||
            !frame.Window.Bounds.Contains(action.X, action.Y))
        { reason = "坐标不在目标窗口内"; return false; }
        if (frame.Package.Gui.Any(item => item.IsPassword &&
            item.Bounds.Contains(action.X, action.Y)))
        { reason = "坐标位于密码输入区域"; return false; }
        reason = "allowed";
        return true;
    }

    public static bool IsAllowedDesktop(AgentLoopFrame frame, AgentToolDecision action,
        WindowInfo? foreground, out string reason)
    {
        if (foreground is null || foreground.Handle != frame.Window.Handle ||
            foreground.ProcessId != frame.Window.ProcessId)
        { reason = "桌面前台已改变，请重新截图"; return false; }
        if (action.Kind is AgentToolKind.TypeText or AgentToolKind.PressKeys or AgentToolKind.Wait)
        { reason = "allowed"; return true; }
        if (action.Kind is not (AgentToolKind.Click or AgentToolKind.RightClick or
            AgentToolKind.DoubleClick or AgentToolKind.Scroll or AgentToolKind.Drag or
            AgentToolKind.Navigate))
        { reason = "不是桌面输入动作"; return false; }
        if (!frame.Bounds.Contains(action.X, action.Y) ||
            action.Kind == AgentToolKind.Drag &&
            !frame.Bounds.Contains(action.TargetX, action.TargetY))
        { reason = "坐标不在桌面截图范围内"; return false; }
        if (frame.Package.Gui.Any(item => item.IsPassword &&
            (item.Bounds.Contains(action.X, action.Y) ||
             action.Kind == AgentToolKind.Drag &&
             item.Bounds.Contains(action.TargetX, action.TargetY))))
        { reason = "坐标位于密码输入区域"; return false; }
        reason = "allowed";
        return true;
    }

    public static bool Execute(AgentToolDecision action, bool preserveUserCursor = false)
    {
        if (action.Kind is not (AgentToolKind.Click or AgentToolKind.RightClick or
            AgentToolKind.DoubleClick)) return false;
        NativePoint old = default;
        bool restore = preserveUserCursor && GetCursorPos(out old);
        using var hidden = preserveUserCursor ? new CursorHideScope() : null;
        if (!SetCursorPos(action.X, action.Y)) return false;
        try
        {
            uint down = action.Kind == AgentToolKind.Click ? LeftDown : RightDown;
            uint up = action.Kind == AgentToolKind.Click ? LeftUp : RightUp;
            var inputs = new[]
            {
                new Input { Type = InputMouse, Mouse = new MouseInput { Flags = down } },
                new Input { Type = InputMouse, Mouse = new MouseInput { Flags = up } }
            };
            Input[] sequence = action.Kind == AgentToolKind.DoubleClick
                ? [..inputs, ..inputs] : inputs;
            uint sent = SendInput((uint)sequence.Length, sequence, Marshal.SizeOf<Input>());
            if (sent == sequence.Length) return true;
            if (sent > 0) SendInput(1, [inputs[1]], Marshal.SizeOf<Input>());
            return false;
        }
        finally
        {
            if (restore && GetCursorPos(out NativePoint current) &&
                current.X == action.X && current.Y == action.Y)
                SetCursorPos(old.X, old.Y);
        }
    }

    public static bool ExecuteScroll(int x, int y, int delta, bool preserveUserCursor)
    {
        NativePoint old = default;
        bool restore = preserveUserCursor && GetCursorPos(out old);
        using var hidden = preserveUserCursor ? new CursorHideScope() : null;
        if (!SetCursorPos(x, y)) return false;
        try
        {
            var input = new Input { Type = InputMouse, Mouse = new MouseInput
            { Flags = Wheel, MouseData = unchecked((uint)delta) } };
            return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
        }
        finally
        {
            if (restore && GetCursorPos(out NativePoint current) &&
                current.X == x && current.Y == y) SetCursorPos(old.X, old.Y);
        }
    }

    public static bool ExecuteDrag(int x, int y, int targetX, int targetY,
        bool preserveUserCursor, CancellationToken cancellationToken,
        int durationMs = 324, string curve = "smooth")
    {
        NativePoint old = default;
        bool restore = preserveUserCursor && GetCursorPos(out old);
        using var hidden = preserveUserCursor ? new CursorHideScope() : null;
        if (!SetCursorPos(x, y)) return false;
        bool down = false;
        try
        {
            down = SendInput(1, [new Input { Type = InputMouse,
                Mouse = new MouseInput { Flags = LeftDown } }], Marshal.SizeOf<Input>()) == 1;
            if (!down) return false;
            for (int step = 1; step <= 18; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double t = step / 18.0;
                double progress = curve switch
                {
                    "linear" => t,
                    "easeOut" => 1 - Math.Pow(1 - t, 3),
                    _ => t * t * (3 - 2 * t)
                };
                double nextX = x + (targetX - x) * progress;
                double nextY = y + (targetY - y) * progress;
                if (curve == "human")
                {
                    double dx = targetX - x, dy = targetY - y;
                    double length = Math.Sqrt(dx * dx + dy * dy);
                    if (length > 1)
                    {
                        double arc = Math.Min(65, length * 0.08) * Math.Sin(Math.PI * progress);
                        nextX -= dy / length * arc;
                        nextY += dx / length * arc;
                    }
                }
                if (!SetCursorPos((int)Math.Round(nextX), (int)Math.Round(nextY))) return false;
                Thread.Sleep(Math.Max(1, durationMs / 18));
            }
            return true;
        }
        finally
        {
            if (down) SendInput(1, [new Input { Type = InputMouse,
                Mouse = new MouseInput { Flags = LeftUp } }], Marshal.SizeOf<Input>());
            if (restore && GetCursorPos(out NativePoint current) &&
                current.X == targetX && current.Y == targetY) SetCursorPos(old.X, old.Y);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    // SendInput targets the system pointer. Keep its brief relocation invisible;
    // the separate blue cursor shows the movement and the original position is restored.
    private sealed class CursorHideScope : IDisposable
    {
        private readonly int _decrements;

        public CursorHideScope()
        {
            for (int i = 0; i < 16; i++)
            {
                _decrements++;
                if (ShowCursor(false) < 0) break;
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < _decrements; i++) ShowCursor(true);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool show);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
