using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace RevitMCP;

internal static class RibbonAssets
{
    public static DrawingImage Icon(int pixels)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RevitMCP.Assets.ooxgn-monogram.svg")
            ?? throw new InvalidOperationException("Embedded OOXGN mark is missing.");
        var svg = XDocument.Load(stream);
        var path = svg.Descendants().Single(element => element.Name.LocalName == "path");
        var geometry = Geometry.Parse(path.Attribute("d")!.Value);
        var mark = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(250, 249, 247)), null, geometry);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(43, 43, 43)), null,
            new RectangleGeometry(new Rect(0, 0, 32, 32))));
        var logo = new DrawingGroup { Transform = new MatrixTransform(0.026, 0, 0, 0.026, 1.8, 12.4) };
        logo.Children.Add(mark);
        group.Children.Add(logo);
        group.Transform = new ScaleTransform(pixels / 32.0, pixels / 32.0);
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    public static DrawingImage AutoConfirmIcon(int pixels)
    {
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(156, 163, 175)), 1.5);
        var tile = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(43, 43, 43)), outline,
            new RectangleGeometry(new Rect(1, 1, 30, 30)));
        var check = new GeometryDrawing(null,
            new Pen(new SolidColorBrush(Color.FromRgb(250, 249, 247)), 3.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round },
            Geometry.Parse("M 7,16 L 13,22 L 25,10"));
        var group = new DrawingGroup();
        group.Children.Add(tile);
        group.Children.Add(check);
        group.Transform = new ScaleTransform(pixels / 32.0, pixels / 32.0);
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
