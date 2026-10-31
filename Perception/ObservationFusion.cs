namespace LingDongDao.Perception;

public static class ObservationFusion
{
    public static DesktopObservation Fuse(CapturedFrame frame, PixelRect analysisBounds,
        IReadOnlyList<GuiElement> gui, IReadOnlyList<OcrText> recognized,
        IReadOnlyList<ActivityEvent> interactions, (int X, int Y) mouse,
        ScreenChange change, IReadOnlyList<string> missing)
    {
        var ocr = recognized.Select(text =>
        {
            GuiElement? parent = gui.Where(element => element.Bounds.Intersects(text.Bounds))
                .OrderBy(element => element.Bounds.Width * (long)element.Bounds.Height)
                .FirstOrDefault();
            return text with { GuiElementName = string.IsNullOrWhiteSpace(parent?.Name)
                ? parent?.Type : parent.Name };
        }).ToArray();
        var candidates = new List<CandidateEvent>();
        if (change.Reasons.Contains("window_switch") && frame.Foreground is { } window)
            candidates.Add(new CandidateEvent("window_switch",
                $"切换到 {window.ProcessName}", "Windows foreground window", false));
        foreach (ActivityEvent click in interactions.Where(item => item.Kind == "mouse_click"))
        {
            GuiElement? target = gui.Where(item => item.Bounds.Contains(click.X, click.Y))
                .OrderBy(item => item.Bounds.Width * (long)item.Bounds.Height).FirstOrDefault();
            if (target is null) continue;
            OcrText? label = ocr.FirstOrDefault(item => target.Bounds.Intersects(item.Bounds));
            string name = !string.IsNullOrWhiteSpace(target.Name) ? target.Name : label?.Text ?? "未命名控件";
            candidates.Add(new CandidateEvent("control_click", $"可能点击 {name}",
                $"mouse_click at ({click.X},{click.Y}); {target.Source} {target.Type} bounds", true));
        }
        var confidence = new Dictionary<string, double>
        {
            ["window_api"] = frame.Foreground is null ? 0 : 1,
            ["change"] = 1,
            ["uia"] = gui.Count == 0 ? 0 : 1,
            ["ocr"] = ocr.Length == 0 ? 0 : ocr.Average(item => item.Confidence)
        };
        return new DesktopObservation(frame.Time, frame.Id, frame.Display, frame.Foreground,
            analysisBounds, gui, ocr, interactions, mouse, change, candidates, missing, confidence);
    }
}
