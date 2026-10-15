using System.Windows;
using System.Windows.Controls;
using CallReceiver.Services;
using CallReceiver.Models;

namespace CallReceiver.Views;

public sealed class RequestDetailWindow : Window
{
    private LogEntry snapshot;
    private readonly RequestLog log;
    public TextBox DetailText { get; } = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(12) };
    public RequestDetailWindow(LogEntry entry, RequestLog log)
    {
        snapshot = entry; this.log = log;
        Title = "요청 상세 정보"; Width = 760; Height = 640; MinWidth = 400; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = DetailText;
        Refresh();
        log.Updated += Updated;
        Closed += (_, _) => log.Updated -= Updated;
    }
    private void Updated(LogEntry value)
    {
        if (value.Id != snapshot.Id) return;
        snapshot = value;
        Refresh();
    }
    private void Refresh() => DetailText.Text = snapshot.Detail?.Describe()
        ?? $"시각: {RequestDiagnostic.Time(snapshot.Time)}\n\n작업: {snapshot.Message}\n\nHTTP 요청이 없는 시스템 로그입니다.";
}
