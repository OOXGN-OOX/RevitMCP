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
    readonly TextBlock version = new() { TextWrapping = TextWrapping.Wrap }, status = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock channelInfo = new() { TextWrapping = TextWrapping.Wrap }, progressText = new() { TextWrapping = TextWrapping.Wrap };
    readonly CheckBox beta = new() { Content = "Получать бета-обновления", Margin = new Thickness(0, 12, 0, 6) };
    readonly ProgressBar progress = new() { Height = 12, Minimum = 0, Maximum = 100 };
    readonly Button check = new() { Content = "Проверить", IsDefault = true }, apply = new() { Content = "Обновить", IsEnabled = false }, rollback = new() { Content = "Вернуть предыдущую" }, cancel = new() { Content = "Отмена", IsEnabled = false };
    readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
    CancellationTokenSource? operation;
    UpdateFeed? available;
    bool closing;
    bool changingSettings;
    UpdateSettings settings = new();

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
        Title = "Revit MCP — обновление"; Width = 600; Height = 470; MinWidth = 480; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        var dark = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(43, 43, 43));
        var light = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(250, 249, 247));
        Background = dark; Foreground = light;
        beta.Foreground = light;
        progress.Foreground = light;
        progress.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : Brushes.Black;
        progress.BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(107, 114, 128));
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 8, 14, 8)));
        buttonStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 8, 8)));
        Resources.Add(typeof(Button), buttonStyle);
        WindowTheme.Apply(this);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Обновление Revit MCP", FontSize = 18, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(version);
        panel.Children.Add(beta);
        channelInfo.Margin = new Thickness(0, 0, 0, 16); panel.Children.Add(channelInfo);
        panel.Children.Add(status); progress.Margin = new Thickness(0, 12, 0, 6); panel.Children.Add(progress);
        progressText.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(progressText);
        var actions = new WrapPanel(); foreach (var button in new[] { check, apply, rollback, cancel }) actions.Children.Add(button);
        panel.Children.Add(actions);
        panel.Children.Add(new TextBlock { Text = "После обновления создайте новые предпросмотры операций.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        Content = new ScrollViewer { Content = panel, Background = Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        status.Text = "Проверка выполняется по кнопке.";
        try { settings = UpdateSettings.Read(CoreHost.ApplicationRoot); }
        catch (Exception ex) { CoreHost.Log(ex.ToString()); status.Text = "Настройки не прочитаны. Бета-канал выключен: " + ex.Message; }
        beta.IsChecked = settings.IncludeBeta;
        RefreshChannel(); RefreshVersion();
        beta.Click += (_, _) =>
        {
            if (changingSettings) return;
            try
            {
                var next = new UpdateSettings { IncludeBeta = beta.IsChecked == true };
                next.Save(CoreHost.ApplicationRoot); settings = next;
                available = null; apply.IsEnabled = false; progress.Value = 0; progressText.Text = "";
                RefreshChannel();
                status.Text = "Канал изменён. Нажмите «Проверить». Установленная сборка не изменена.";
            }
            catch (Exception ex)
            {
                changingSettings = true; beta.IsChecked = settings.IncludeBeta; changingSettings = false;
                CoreHost.Log(ex.ToString()); status.Text = "Не удалось сохранить выбор канала: " + ex.Message;
            }
        };
        check.Click += async (_, _) => await Run(async token =>
        {
            available = null;
            status.Text = "Проверка выбранного канала…";
            var feed = await new UpdateClient(http, CoreHost.ApplicationRoot).CheckAsync(settings.Channel, token);
            if (feed is null) { status.Text = "В выбранном канале пока нет опубликованных обновлений."; return; }
            if (!CoreHost.Modules.IsCompatible(feed.Manifest)) { status.Text = "Для этой сборки нужен новый установщик Revit MCP. Закройте Revit и установите полный пакет."; return; }
            if (CoreHost.Modules.IsNewer(feed.Manifest)) { available = feed; status.Text = $"Доступна версия {feed.Manifest.Version}, сборка {feed.Manifest.BuildId[..8]} ({ChannelLabel(feed.Manifest)})."; }
            else status.Text = "В выбранном канале нет более новой сборки. Установленная сборка не изменена.";
        });
        apply.Click += async (_, _) => await Run(async token =>
        {
            var feed = available ?? throw new InvalidOperationException("Сначала проверьте обновления.");
            var receiving = true;
            var report = new Progress<UpdateProgress>(value => { if (receiving && !closing && !token.IsCancellationRequested) PresentProgress(value); });
            InstalledVersion next;
            try { next = await new UpdateClient(http, CoreHost.ApplicationRoot).StageAsync(feed, report, token, settings.Channel); }
            finally { receiving = false; }
            token.ThrowIfCancellationRequested();
            PresentWaiting();
            await CoreHost.Dispatcher.InvokeAsync(_ =>
            {
                token.ThrowIfCancellationRequested();
                status.Text = "Переключение рабочей части…"; progressText.Text = "Применение обновления";
                CoreHost.Activate(next);
            }, token);
            available = null; RefreshVersion(); progress.Value = 100; progressText.Text = "Готово — 100%";
            status.Text = "Обновление установлено. Можно продолжать работу.";
        });
        rollback.Click += async (_, _) => await Run(async token =>
        {
            status.Text = "Ожидание Revit для возврата предыдущей сборки…";
            progressText.Text = "Завершите текущую команду или открытый диалог Revit.";
            await CoreHost.Dispatcher.InvokeAsync(_ => CoreHost.Rollback(), token);
            available = null; RefreshVersion(); status.Text = "Предыдущая сборка восстановлена.";
        });
        cancel.Click += (_, _) => { operation?.Cancel(); cancel.IsEnabled = false; };
        PreviewKeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Escape) { Close(); args.Handled = true; } };
        Closed += (_, _) => { closing = true; operation?.Cancel(); http.Dispose(); active = null; };
        Loaded += (_, _) => check.Focus();
    }

    void RefreshVersion()
    {
        var installed = CoreHost.Modules.Current;
        version.Text = installed is null ? "Рабочая часть не загружена." : $"Установлено: {installed.Manifest.Version} · {installed.Manifest.BuildId[..8]} · {ChannelLabel(installed.Manifest)}";
        rollback.IsEnabled = CoreHost.Modules.CanRollback;
    }

    static string ChannelLabel(UpdateManifest manifest) => manifest.Channel switch { "stable" => "стабильная", "beta" => "бета, " + manifest.SourceBranch, _ => "прежняя тестовая сборка" };

    void RefreshChannel() => channelInfo.Text = settings.IncludeBeta
        ? "Бета-канал: тестовые сборки из ветки разработки."
        : "Стабильный канал: опубликованные выпуски из main.";

    internal void PresentProgress(UpdateProgress value)
    {
        progress.IsIndeterminate = value.Total <= 0;
        progress.Value = value.Total > 0 ? Math.Clamp(value.Completed * 100.0 / value.Total, 0, 100) : 0;
        switch (value.Stage)
        {
            case UpdateStage.Downloading:
                status.Text = "Загрузка обновления…";
                progressText.Text = $"{progress.Value:0}% · {value.Completed / 1048576.0:0.00} из {value.Total / 1048576.0:0.00} МБ";
                break;
            case UpdateStage.Verifying:
                status.Text = "Проверка контрольной суммы…"; progressText.Text = "Проверка целостности загруженного пакета";
                break;
            case UpdateStage.Extracting:
                status.Text = "Распаковка и проверка файлов…"; progressText.Text = $"Файлы: {value.Completed} из {value.Total}";
                break;
        }
    }

    internal void PresentWaiting()
    {
        status.Text = "Ожидание завершения текущей операции Revit…"; progress.IsIndeterminate = true;
        progressText.Text = "Пакет проверен. Завершите текущую команду или открытый диалог Revit.";
    }

    async Task Run(Func<CancellationToken, Task> action)
    {
        if (operation is not null) return;
        using var source = new CancellationTokenSource(); operation = source;
        check.IsEnabled = apply.IsEnabled = rollback.IsEnabled = beta.IsEnabled = false; cancel.IsEnabled = true;
        progress.Value = 0; progressText.Text = ""; progress.IsIndeterminate = true;
        try { await action(source.Token); }
        catch (OperationCanceledException) { if (!closing) { status.Text = "Операция отменена."; progressText.Text = ""; progress.Value = 0; } }
        catch (Exception ex) { CoreHost.Log(ex.ToString()); if (!closing) { status.Text = "Не удалось выполнить обновление: " + ex.Message; progressText.Text = ""; progress.Value = 0; } }
        finally
        {
            operation = null;
            if (!closing) { check.IsEnabled = beta.IsEnabled = true; apply.IsEnabled = available is not null; rollback.IsEnabled = CoreHost.Modules.CanRollback; cancel.IsEnabled = false; progress.IsIndeterminate = false; }
        }
    }
}
