using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevitMCP.Loader;
using RevitMCP.Updates;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        foreach (var dpi in new[] { 96, 144 })
        foreach (var size in new[] { new Size(584, 432), new Size(464, 342) })
        foreach (var state in new[] { "ready", "download", "verify", "extract", "waiting" })
        {
            using var scope = new CloseWindow(new UpdateWindow());
            var window = (UpdateWindow)scope.Window;
            switch (state)
            {
                case "download": window.PresentProgress(new(UpdateStage.Downloading, 5242880, 10485760)); break;
                case "verify": window.PresentProgress(new(UpdateStage.Verifying)); break;
                case "extract": window.PresentProgress(new(UpdateStage.Extracting, 12, 26)); break;
                case "waiting": window.PresentWaiting(); break;
            }
            var content = (FrameworkElement)scope.Window.Content;
            content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)size.Width * dpi / 96, (int)size.Height * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(args[0], $"update-window-{dpi}-{size.Width}-{state}.png")); encoder.Save(file);
            foreach (var child in Descendants(content).OfType<Button>())
                if (child.ActualWidth < 60 || child.ActualHeight < 30) throw new Exception("Button clipped: " + child.Content);
            var checkbox = Descendants(content).OfType<CheckBox>().Single();
            if (checkbox.IsChecked != false) throw new Exception("Beta must be opt-in");
            var bar = Descendants(content).OfType<ProgressBar>().Single();
            if (bar.ActualWidth < 200 || bar.ActualHeight < 10) throw new Exception("Progress is not visible");
            if (state == "download" && (bar.IsIndeterminate || bar.Value != 50)) throw new Exception("Download percentage incorrect");
            if ((state == "waiting" || state == "verify") && !bar.IsIndeterminate) throw new Exception("Stage must use indeterminate progress");
            var scroll = (ScrollViewer)content;
            if (scroll.ExtentWidth > scroll.ViewportWidth + 1) throw new Exception("Horizontal clipping");
            if (size.Width == 584 && scroll.ScrollableHeight > 1) throw new Exception("Default window should fit its controls");
            Console.WriteLine($"PASS update window {state}, {size.Width} DIP, {dpi} dpi");
        }
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject item)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
        {
            var child = VisualTreeHelper.GetChild(item, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    sealed record CloseWindow(Window Window) : IDisposable { public void Dispose() => Window.Close(); }
}

namespace RevitMCP.Loader
{
    static class CoreHost
    {
        public static string ApplicationRoot { get; } = Path.Combine(Path.GetTempPath(), "RevitMCP-window-check-" + Guid.NewGuid().ToString("N"));
        public static RevitMCP.Updates.ModuleManager Modules { get; } = new(ApplicationRoot, new Version(0, 0, 2, 1));
        public static TestDispatcher Dispatcher { get; } = new();
        public static void Activate(RevitMCP.Updates.InstalledVersion value) => throw new NotSupportedException();
        public static void Rollback() => throw new NotSupportedException();
        public static void Log(string value) => Console.Error.WriteLine(value);
    }
    sealed class TestDispatcher { public Task InvokeAsync(Action<object> action, CancellationToken token) => throw new NotSupportedException(); }
}
