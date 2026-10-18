using System.Windows;
using System.Windows.Controls;

namespace CallReceiver.Views;

public sealed class StorageRecoveryWindow : Window
{
    public string? SelectedDirectory { get; private set; }
    public StorageRecoveryWindow(string message, string directory)
    {
        Title = "전화 수신 설정 — 저장소 복구";
        Width = 600; SizeToContent = SizeToContent.Height; MinWidth = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "기존 설정과 DB는 삭제하지 않습니다. 같은 위치에서 다시 시도하거나 사용할 설정 폴더를 직접 선택하세요.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        void Add(string text, Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) };
            button.Click += (_, _) => action(); buttons.Children.Add(button);
        }
        Add("다시 시도", () => { SelectedDirectory = directory; DialogResult = true; });
        Add("설정 폴더 선택", () =>
        {
            using var picker = new System.Windows.Forms.FolderBrowserDialog
                { Description = "설정과 DB를 사용할 폴더를 직접 선택하세요.", SelectedPath = directory };
            if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            { SelectedDirectory = picker.SelectedPath; DialogResult = true; }
        });
        Add("종료", () => DialogResult = false);
        panel.Children.Add(buttons); Content = panel;
    }
}
