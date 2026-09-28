using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CallReceiver.ViewModels;

namespace CallReceiver.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private readonly Func<Task> exit;
    public bool AllowClose { get; set; }
    public MainWindow(MainViewModel model, Func<Task> exit)
    {
        InitializeComponent();
        DataContext = this.model = model;
        this.exit = exit;
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            if (model.Saved.MinimizeToTray) Hide();
            else _ = exit();
        }
        base.OnClosing(e);
    }
    private void IntegerTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(c => !char.IsAsciiDigit(c));
    private void IntegerPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) ||
            e.DataObject.GetData(DataFormats.UnicodeText) is not string value ||
            value.Length == 0 || value.Any(c => !char.IsAsciiDigit(c)))
            e.CancelCommand();
    }
}
