using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using MeasFlow.Viewer.Models;
using MeasFlow.Viewer.ViewModels;
using ScottPlot.Avalonia;

namespace MeasFlow.Viewer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Wire drag-drop
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);

        ActualThemeVariantChanged += (_, _) => ApplyPlotTheme();
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
            vm.PlotRequested += data => UpdatePlotControl(this.FindControl<AvaPlot>("MainPlot"), data, IsDarkMode);
            vm.SignalPlotRequested += data => UpdatePlotControl(this.FindControl<AvaPlot>("SignalPlot"), data, IsDarkMode);
        }
    }

    private bool IsDarkMode => ActualThemeVariant == ThemeVariant.Dark;

    private void ApplyPlotTheme()
    {
        var mainPlot = this.FindControl<AvaPlot>("MainPlot");
        var signalPlot = this.FindControl<AvaPlot>("SignalPlot");
        bool isDark = IsDarkMode;
        ApplyPlotStyle(mainPlot, isDark);
        ApplyPlotStyle(signalPlot, isDark);
    }

    private static void ApplyPlotStyle(AvaPlot? avaPlot, bool isDark)
    {
        if (avaPlot == null) return;
        ApplyTheme(avaPlot.Plot, isDark);
        avaPlot.Refresh();
    }

    private static void ApplyTheme(ScottPlot.Plot plot, bool isDark)
    {
        if (isDark)
        {
            plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1e1e1e");
            plot.DataBackground.Color   = ScottPlot.Color.FromHex("#252526");
            plot.Axes.Color(ScottPlot.Colors.LightGray);
            plot.Grid.MajorLineColor = ScottPlot.Colors.LightGray.WithOpacity(0.15);
        }
        else
        {
            plot.FigureBackground.Color = ScottPlot.Colors.White;
            plot.DataBackground.Color   = ScottPlot.Colors.White;
            plot.Axes.Color(ScottPlot.Colors.Black);
            plot.Grid.MajorLineColor = ScottPlot.Colors.Black.WithOpacity(0.1);
        }
    }

    private static readonly ScottPlot.Color[] SeriesColors =
    [
        ScottPlot.Color.FromHex("#1f77b4"),
        ScottPlot.Color.FromHex("#ff7f0e"),
        ScottPlot.Color.FromHex("#2ca02c"),
        ScottPlot.Color.FromHex("#d62728"),
        ScottPlot.Color.FromHex("#9467bd"),
        ScottPlot.Color.FromHex("#8c564b"),
        ScottPlot.Color.FromHex("#e377c2"),
        ScottPlot.Color.FromHex("#7f7f7f"),
        ScottPlot.Color.FromHex("#bcbd22"),
        ScottPlot.Color.FromHex("#17becf"),
    ];

    private static void UpdatePlotControl(AvaPlot? avaPlot, PlotData data, bool isDark)
    {
        if (avaPlot == null) return;

        var plot = avaPlot.Plot;
        plot.Clear();

        for (int s = 0; s < data.Series.Count; s++)
        {
            var series = data.Series[s];
            double[] ys = series.YData;
            double[] xs;

            int step = Math.Max(1, ys.Length / 100_000);
            if (step > 1)
            {
                int count = (ys.Length + step - 1) / step;
                var dsX = new double[count];
                var dsY = new double[count];
                int j = 0;
                for (int i = 0; i < ys.Length && j < count; i += step, j++)
                {
                    dsX[j] = series.XData != null ? series.XData[i] : i;
                    dsY[j] = ys[i];
                }
                xs = dsX[..j];
                ys = dsY[..j];
            }
            else
            {
                if (series.XData != null)
                {
                    xs = series.XData;
                }
                else
                {
                    xs = new double[ys.Length];
                    for (int i = 0; i < xs.Length; i++) xs[i] = i;
                }
            }

            var sc = plot.Add.ScatterLine(xs, ys);
            sc.LineWidth = 1.5f;
            sc.Color = SeriesColors[s % SeriesColors.Length];
            sc.LegendText = series.Name;
        }

        plot.Legend.IsVisible = data.Series.Count > 1;

        if (!string.IsNullOrEmpty(data.Title))
            plot.Title(data.Title);
        plot.XLabel(data.XLabel);
        plot.YLabel(data.YLabel);
        plot.Axes.AutoScale();

        ApplyTheme(plot, isDark);
        avaPlot.Refresh();
    }

    private async Task OpenFileDialog()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open MeasFlow File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("MeasFlow Files") { Patterns = ["*.meas"] },
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
            if (path != null && path.EndsWith(".meas", StringComparison.OrdinalIgnoreCase)
                && DataContext is MainWindowViewModel vm)
            {
                vm.OpenFile(path);
            }
        }
    }
}
