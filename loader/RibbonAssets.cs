using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitMCP.Loader;

internal static class RibbonAssets
{
    public static ImageSource? Load(string name, int size)
    {
        try
        {
            var fileName = name + "-" + size + ".png";
            var directory = CoreHost.Modules.ActiveDirectory;
            using var stream = directory is not null && File.Exists(Path.Combine(directory, "assets", fileName))
                ? File.OpenRead(Path.Combine(directory, "assets", fileName))
                : Assembly.GetExecutingAssembly().GetManifestResourceStream("RevitMCP.Loader.Assets." + fileName);
            if (stream is null) return null;
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.DecodePixelWidth = size; bitmap.EndInit(); bitmap.Freeze();
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(43, 43, 43)), null,
                new RectangleGeometry(new Rect(0, 0, size, size))));
            group.Children.Add(new ImageDrawing(bitmap, new Rect(0, 0, size, size)));
            group.Freeze();
            var image = new DrawingImage(group); image.Freeze(); return image;
        }
        catch (Exception ex) { CoreHost.Log("Icon: " + ex.Message); return null; }
    }
}
