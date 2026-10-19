using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using RevitMCP.Updates;

namespace RevitMCP.Loader;

internal sealed class UpdateWindow : Window
{
    static UpdateWindow? active;
    readonly TextBlock version = new(), status = new() { TextWrapping = TextWrapping.Wrap };
    readonly ProgressBar progress = new() { Height = 4, Minimum = 0, Maximum = 100 };
    readonly Button check = new() { Content = "Проверить", IsDefault = true }, apply = new() { Content = "Обновить", IsEnabled = false }, rollback = new() { Content = "Вернуть предыдущую" }, cancel = new() { Content = "Отмена", IsEnabled = false };
    readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
    CancellationTokenSource? operation;
    UpdateFeed? available;
    bool closing;

    public static void Open(nint owner)
    {
        if (active is not null) { active.Activate(); return; }
        active = new UpdateWindow();
        new WindowInteropHelper(active).Owner = owner;
        active.Show();
    }
    public static void CloseActive() => active?.Close();

    internal UpdateWindow()
    {
        Title = "Revit MCP — обновление"; Width = 560; Height = 340; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        var dark = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(43, 43, 43));
        var light = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(250, 249, 247));
        Background = dark; Foreground = light;
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 8, 14, 8)));
        buttonStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 8, 8)));
        Resources.Add(typeof(Button), buttonStyle);
        WindowTheme.Apply(this);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Обновление Revit MCP", FontSize = 21, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(version);
        panel.Children.Add(new TextBlock { Text = "Канал тестирования 0.0.2", Margin = new Thickness(0, 6, 0, 16) });
        panel.Children.Add(status); progress.Margin = new Thickness(0, 16, 0, 16); panel.Children.Add(progress);
        var actions = new WrapPanel(); foreach (var button in new[] { check, apply, rollback, cancel }) actions.Children.Add(button);
        panel.Children.Add(actions);
        panel.Children.Add(new TextBlock { Text = "После обновления создайте новые предпросмотры операций.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        Content = new ScrollViewer { Content = panel, Background = Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        status.Text = "Проверка выполняется по кнопке."; RefreshVersion();
        check.Click += async (_, _) => await Run(async token =>
        {
            available = null;
            var feed = await new UpdateClient(http, CoreHost.ApplicationRoot).CheckAsync(token);
            if (CoreHost.Modules.IsNewer(feed.Manifest)) { available = feed; status.Text = $"Доступна версия {feed.Manifest.Version}, сборка {feed.Manifest.BuildId[..8]}."; }
            else status.Text = "Установлена актуальная сборка.";
        });
        apply.Click += async (_, _) => await Run(async token =>
        {
            var feed = available ?? throw new InvalidOperationException("Сначала проверьте обновления.");
            status.Text = "Загрузка и проверка пакета…"; progress.IsIndeterminate = false;
            var next = await new UpdateClient(http, CoreHost.ApplicationRoot).StageAsync(feed, new Progress<long>(value => progress.Value = value * 100.0 / feed.Size), token);
            status.Text = "Ожидание завершения текущей операции Revit…"; progress.IsIndeterminate = true;
            await CoreHost.Dispatcher.InvokeAsync(_ => CoreHost.Activate(next), token);
            available = null; RefreshVersion(); status.Text = "Обновление установлено. Можно продолжать работу.";
        });
        rollback.Click += async (_, _) => await Run(async token =>
        {
            status.Text = "Переключение на предыдущую сборку…";
            await CoreHost.Dispatcher.InvokeAsync(_ => CoreHost.Rollback(), token);
            available = null; RefreshVersion(); status.Text = "Предыдущая сборка восстановлена.";
        });
        cancel.Click += (_, _) => operation?.Cancel();
        PreviewKeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Escape) { Close(); args.Handled = true; } };
        Closed += (_, _) => { closing = true; operation?.Cancel(); http.Dispose(); active = null; };
        Loaded += (_, _) => check.Focus();
    }

    void RefreshVersion()
    {
        var installed = CoreHost.Modules.Current;
        version.Text = installed is null ? "Рабочая часть не загружена." : $"Установлено: {installed.Manifest.Version} · {installed.Manifest.BuildId[..8]}";
        rollback.IsEnabled = CoreHost.Modules.CanRollback;
    }

    async Task Run(Func<CancellationToken, Task> action)
    {
        if (operation is not null) return;
        using var source = new CancellationTokenSource(); operation = source;
        check.IsEnabled = apply.IsEnabled = rollback.IsEnabled = false; cancel.IsEnabled = true; progress.IsIndeterminate = true;
        try { await action(source.Token); }
        catch (OperationCanceledException) { if (!closing) status.Text = "Операция отменена."; }
        catch (Exception ex) { CoreHost.Log(ex.ToString()); if (!closing) status.Text = "Не удалось выполнить обновление: " + ex.Message; }
        finally
        {
            operation = null;
            if (!closing) { check.IsEnabled = true; apply.IsEnabled = available is not null; rollback.IsEnabled = CoreHost.Modules.CanRollback; cancel.IsEnabled = false; progress.IsIndeterminate = false; }
        }
    }
}
