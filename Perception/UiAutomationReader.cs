using System.Windows.Automation;

namespace LingDongDao.Perception;

public sealed class UiAutomationReader
{
    public IReadOnlyList<GuiElement> Read(WindowInfo? window, int maximumElements = 100)
    {
        if (window is null || window.Handle == 0 || !window.Visible) return [];
        AutomationElement root;
        try { root = AutomationElement.FromHandle(new IntPtr(window.Handle)); }
        catch (Exception) { return []; }
        var output = new List<GuiElement>();
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        TreeWalker walker = TreeWalker.ControlViewWalker;
        while (queue.Count > 0 && output.Count < maximumElements)
        {
            (AutomationElement element, int depth) = queue.Dequeue();
            try
            {
                var current = element.Current;
                var bounds = current.BoundingRectangle;
                if (bounds.Width >= 2 && bounds.Height >= 2 && !current.IsOffscreen)
                {
                    var rect = new PixelRect((int)Math.Round(bounds.X), (int)Math.Round(bounds.Y),
                        (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
                    bool password = current.IsPassword;
                    output.Add(new GuiElement(password ? "[password]" : current.Name ?? "",
                        current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
                        rect, current.IsEnabled, password, "uia", null));
                }
                if (depth >= 3) continue;
                for (AutomationElement? child = walker.GetFirstChild(element); child is not null;
                     child = walker.GetNextSibling(child))
                {
                    queue.Enqueue((child, depth + 1));
                    if (queue.Count + output.Count >= maximumElements * 2) break;
                }
            }
            catch (ElementNotAvailableException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        return output;
    }
}
