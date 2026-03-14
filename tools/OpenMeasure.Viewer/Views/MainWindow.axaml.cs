using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenMeasure.Viewer.Models;
using OpenMeasure.Viewer.ViewModels;
using ScottPlot.Avalonia;

namespace OpenMeasure.Viewer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Wire drag-drop
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Wire menu items
        var menuOpen = this.FindControl<MenuItem>("MenuOpen");
        var menuExit = this.FindControl<MenuItem>("MenuExit");

        if (menuOpen != null) menuOpen.Click += async (_, _) => await OpenFileDialog();
        if (menuExit != null) menuExit.Click += (_, _) => Close();

        // Wire plot updates from ViewModel
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PlotRequested += OnPlotRequested;
        }
    }

    private void OnPlotRequested(PlotData data)
    {
        var mainPlot = this.FindControl<AvaPlot>("MainPlot");
        var signalPlot = this.FindControl<AvaPlot>("SignalPlot");

        // Update both plot controls with the same data
        UpdatePlotControl(mainPlot, data);
        UpdatePlotControl(signalPlot, data);
    }

    private static void UpdatePlotControl(AvaPlot? avaPlot, PlotData data)
    {
        if (avaPlot == null) return;

        var plot = avaPlot.Plot;
        plot.Clear();

        // Build x/y arrays
        double[] xs;
        double[] ys = data.YData;

        // For large datasets, downsample for display
        int step = Math.Max(1, ys.Length / 100_000);
        if (step > 1)
        {
            int count = (ys.Length + step - 1) / step;
            var dsX = new double[count];
            var dsY = new double[count];
            int j = 0;
            for (int i = 0; i < ys.Length && j < count; i += step, j++)
            {
                dsX[j] = data.XData != null ? data.XData[i] : i;
                dsY[j] = ys[i];
            }
            xs = dsX[..j];
            ys = dsY[..j];
        }
        else
        {
            if (data.XData != null)
            {
                xs = data.XData;
            }
            else
            {
                xs = new double[ys.Length];
                for (int i = 0; i < xs.Length; i++) xs[i] = i;
            }
        }

        var sig = plot.Add.ScatterLine(xs, ys);
        sig.LineWidth = 1.5f;
        sig.Color = ScottPlot.Color.FromHex("#0078D7");

        plot.Title(data.Title);
        plot.XLabel(data.XLabel);
        plot.YLabel(data.YLabel);
        plot.Axes.AutoScale();

        avaPlot.Refresh();
    }

    private async Task OpenFileDialog()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open OMX File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("OpenMeasure Files") { Patterns = ["*.omx"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] },
            ]
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (path != null && DataContext is MainWindowViewModel vm)
            {
                vm.OpenFile(path);
            }
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // Suppress deprecation warning for Avalonia 11 compat
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
#pragma warning restore CS0618
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        var files = e.Data.GetFiles()?.ToList();
#pragma warning restore CS0618
        if (files is { Count: > 0 })
        {
            var path = files[0].TryGetLocalPath();
            if (path != null && path.EndsWith(".omx", StringComparison.OrdinalIgnoreCase)
                && DataContext is MainWindowViewModel vm)
            {
                vm.OpenFile(path);
            }
        }
    }
}
