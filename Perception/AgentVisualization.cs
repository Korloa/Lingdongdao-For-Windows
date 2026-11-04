using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LingDongDao.Perception;

public sealed record AgentVisualizationRequest(string Title, string Html, string Css,
    string JavaScript, int Width = 820, int Height = 520, int DurationSeconds = 0);

public sealed record AgentVisualizationResult(bool Shown, string Detail);

public static partial class AgentVisualizationDocument
{
    [GeneratedRegex(@"<\s*/?\s*(?:script|style|iframe|frame|object|embed|form|base|meta|link)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenHtmlTags();

    public static bool TryValidate(AgentVisualizationRequest request, out string reason)
    {
        reason = "";
        if (request.Title.Length is < 1 or > 80 || request.Html.Length is < 1 or > 24000 ||
            request.Css.Length > 16000 || request.JavaScript.Length > 24000 ||
            request.Width is < 480 or > 1200 || request.Height is < 320 or > 760 ||
            request.DurationSeconds is < 0 or > 300)
        { reason = "可视化内容或面板尺寸超出限制"; return false; }
        string content = string.Concat(request.Title, request.Html, request.Css,
            request.JavaScript);
        if (Encoding.UTF8.GetByteCount(content) > 64000 ||
            content.Any(character => char.IsControl(character) &&
                character is not ('\r' or '\n' or '\t')))
        { reason = "可视化内容过大或含无效控制字符"; return false; }
        if (PrivacyFilter.ContainsSecret(content))
        { reason = "可视化内容疑似包含密钥"; return false; }
        if (ForbiddenHtmlTags().IsMatch(request.Html) ||
            request.Css.Contains("</style", StringComparison.OrdinalIgnoreCase) ||
            request.JavaScript.Contains("</script", StringComparison.OrdinalIgnoreCase))
        { reason = "HTML 只能提供页面片段；CSS 和 JS 请分别填写"; return false; }
        return true;
    }

    public static string Build(AgentVisualizationRequest request)
    {
        if (!TryValidate(request, out string reason)) throw new ArgumentException(reason);
        string title = WebUtility.HtmlEncode(request.Title);
        return "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; " +
            "script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; " +
            "font-src data:; connect-src 'none'; media-src 'none'; object-src 'none'; " +
            "frame-src 'none'; worker-src 'none'; form-action 'none'; base-uri 'none'\">" +
            "<title>" + title + "</title><style>" +
            "html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#0b1320;color:#eef7ff;" +
            "font-family:'Microsoft YaHei UI',system-ui,sans-serif}*{box-sizing:border-box}" +
            "#stage{position:relative;width:100%;height:100%;overflow:hidden}" + request.Css +
            "</style></head><body><main id=\"stage\">" + request.Html + "</main><script>" +
            "window.visual=Object.freeze({" +
            "animate:(selector,frames,options)=>document.querySelector(selector)?.animate(frames,options).finished," +
            "stagger:(selector,frames,options,gap=90)=>Promise.all([...document.querySelectorAll(selector)].map((el,i)=>el.animate(frames,{...options,delay:(options?.delay||0)+i*gap}).finished))," +
            "count:(selector,from,to,duration=1200)=>new Promise(resolve=>{const el=document.querySelector(selector);if(!el){resolve();return}const start=performance.now();function tick(now){const t=Math.min(1,(now-start)/duration);el.textContent=Math.round(from+(to-from)*t).toLocaleString();if(t<1)requestAnimationFrame(tick);else resolve()}requestAnimationFrame(tick)})," +
            "sleep:ms=>new Promise(resolve=>setTimeout(resolve,ms))});" +
            request.JavaScript + "</script></body></html>";
    }
}
