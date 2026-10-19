using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevitMCP.Loader;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        foreach (var dpi in new[] { 96, 144 })
        {
            using var scope = new CloseWindow(new UpdateWindow());
            var content = (FrameworkElement)scope.Window.Content;
            content.Measure(new Size(544, 302)); content.Arrange(new Rect(0, 0, 544, 302)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(544 * dpi / 96, 302 * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(args[0], $"update-window-{dpi}.png")); encoder.Save(file);
            foreach (var child in Descendants(content).OfType<Button>())
                if (child.ActualWidth < 60 || child.ActualHeight < 30) throw new Exception("Button clipped: " + child.Content);
            Console.WriteLine("PASS update window layout at " + dpi + " dpi");
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
        public static string ApplicationRoot => Path.Combine(Path.GetTempPath(), "RevitMCP-window-check");
        public static RevitMCP.Updates.ModuleManager Modules { get; } = new(ApplicationRoot, new Version(0, 0, 2));
        public static TestDispatcher Dispatcher { get; } = new();
        public static void Activate(RevitMCP.Updates.InstalledVersion value) => throw new NotSupportedException();
        public static void Rollback() => throw new NotSupportedException();
        public static void Log(string value) => Console.Error.WriteLine(value);
    }
    sealed class TestDispatcher { public Task InvokeAsync(Action<object> action, CancellationToken token) => throw new NotSupportedException(); }
}
