using System.ComponentModel;
using System.Windows;
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
}
