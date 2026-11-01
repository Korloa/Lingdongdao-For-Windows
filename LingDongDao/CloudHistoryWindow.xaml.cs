using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LingDongDao.Perception;

namespace LingDongDao;

public partial class CloudHistoryWindow : Window
{
    private readonly PerceptionService _perception;

    internal CloudHistoryWindow(PerceptionService perception)
    {
        InitializeComponent();
        _perception = perception;
        _perception.CloudHistoryChanged += OnHistoryChanged;
        Closed += (_, _) => _perception.CloudHistoryChanged -= OnHistoryChanged;
        RefreshRecords();
    }

    private void OnHistoryChanged() => Dispatcher.BeginInvoke(new Action(RefreshRecords));
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRecords();
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this, "删除本机保存的调用截图和模型回复？云端费用额度记录会保留。",
            "清空调用记录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { _perception.ClearCloudHistory(); }
        catch (Exception exception)
        { System.Windows.MessageBox.Show(this, "清空失败：" + exception.GetType().Name); }
    }

    private void RefreshRecords()
    {
        string? selectedId = (CallsList.SelectedItem as CloudCallRecord)?.Id;
        IReadOnlyList<CloudCallRecord> records = _perception.CloudHistory();
        CallsList.ItemsSource = records;
        CallsList.SelectedItem = records.FirstOrDefault(item => item.Id == selectedId)
            ?? records.FirstOrDefault();
        if (records.Count == 0) ShowRecord(null);
    }

    private void CallsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowRecord(CallsList.SelectedItem as CloudCallRecord);

    private void ShowRecord(CloudCallRecord? record)
    {
        RecordMeta.Text = record is null ? "暂无调用记录" :
            $"{record.Time:yyyy-MM-dd HH:mm:ss} · {record.SourceLabel} · {record.ProcessName}\n{record.WindowTitle}";
        RecordStatus.Text = record is null ? "" :
            $"{record.StatusLabel} · 事件：{record.EventSummary} · 本地分数：{record.LocalScore:0.00}";
        CurrentScreenshot.Source = Decode(record?.CurrentJpeg);
        BeforeScreenshot.Source = Decode(record?.BeforeJpeg);
        BeforeSection.Visibility = BeforeScreenshot.Source is null ? Visibility.Collapsed : Visibility.Visible;
        ScreenshotNote.Text = record?.CurrentJpeg is null
            ? "此调用没有可恢复的截图。" : "此图为实际发送给云端的当前窗口截图。";
        DecisionText.Text = record?.ShouldIntervene switch
        {
            true => "建议主动提醒",
            false => "本次决定不打断用户",
            _ => "未取得有效判断"
        };
        ReasonText.Text = string.IsNullOrWhiteSpace(record?.Reason) ? "—" : record.Reason;
        SuggestionText.Text = string.IsNullOrWhiteSpace(record?.Suggestion) ? "—" : record.Suggestion;
        ActionText.Text = string.IsNullOrWhiteSpace(record?.RecommendedAction) ? "—" : record.RecommendedAction;
        RawResponseText.Text = string.IsNullOrWhiteSpace(record?.RawResponse) ? "—" : record.RawResponse;
        ErrorText.Text = record?.Error ?? "";
    }

    private static BitmapImage? Decode(byte[]? jpeg)
    {
        if (jpeg is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(jpeg);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }
}
