using System.Runtime.InteropServices;

namespace LingDongDao.Perception;

public static class AgentMouseTool
{
    private const uint InputMouse = 0;
    private const uint LeftDown = 0x0002, LeftUp = 0x0004;
    private const uint RightDown = 0x0008, RightUp = 0x0010;

    public static bool IsAllowed(AgentLoopFrame frame, AgentToolDecision action,
        WindowInfo? foreground, out string reason)
    {
        if (action.Kind is not (AgentToolKind.Click or AgentToolKind.RightClick))
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

    public static bool Execute(AgentToolDecision action)
    {
        if (!SetCursorPos(action.X, action.Y)) return false;
        uint down = action.Kind == AgentToolKind.Click ? LeftDown : RightDown;
        uint up = action.Kind == AgentToolKind.Click ? LeftUp : RightUp;
        var inputs = new[]
        {
            new Input { Type = InputMouse, Mouse = new MouseInput { Flags = down } },
            new Input { Type = InputMouse, Mouse = new MouseInput { Flags = up } }
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return true;
        if (sent == 1) SendInput(1, [inputs[1]], Marshal.SizeOf<Input>());
        return false;
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
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
