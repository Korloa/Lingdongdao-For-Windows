namespace LingDongDao;

internal static class IdleHoverZone
{
    public static double Width(AppSettings settings) =>
        settings.LineWidth + settings.HoverHorizontalPadding * 2;

    public static double EdgeHeight(AppSettings settings) =>
        settings.LineThickness + settings.HoverVerticalPadding;

    public static bool Contains(AppSettings settings, double windowWidth, double x, double y)
    {
        double width = Width(settings);
        double left = (windowWidth - width) / 2;
        if (x < left || x > left + width) return false;
        double lineTop = Math.Max(0, settings.TopOffset - settings.HoverVerticalPadding / 2);
        double lineBottom = settings.TopOffset + settings.LineThickness + settings.HoverVerticalPadding / 2;
        return (y >= 0 && y <= EdgeHeight(settings)) ||
               (y >= lineTop && y <= lineBottom);
    }
}
