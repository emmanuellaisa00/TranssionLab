using System.Windows;
using TranssionLab.ViewModels;

namespace TranssionLab.App;

public partial class MainWindow : Window
{
    public MainWindow(MainShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            try
            {
                await viewModel.GlobalScanDevicesCommand.ExecuteAsync().ConfigureAwait(true);
            }
            catch
            {
                // Startup scan errors are already captured in StatusBanner and StructuredLogger
            }
        };
        Closing += (_, _) =>
        {
            if (viewModel.CancelWaitingCommand.CanExecute(null))
            {
                viewModel.CancelWaitingCommand.Execute(null);
            }
        };
    }
}
