using RevitMCP.Setup;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Directory.CreateDirectory(args[0]);
        foreach (var scale in new[] { 1f, 1.5f })
        {
            using var form = new SetupWindow(false);
            form.AutoScaleMode = AutoScaleMode.None;
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000);
            form.Show(); Application.DoEvents(); form.PerformLayout();
            var fonts = Children(form).Prepend(form).Select(control => (Control: control, Font: control.Font)).ToArray();
            form.Scale(new SizeF(scale, scale));
            if (scale != 1) foreach (var entry in fonts) entry.Control.Font = new Font(entry.Font.FontFamily, entry.Font.Size * scale, entry.Font.Style);
            form.PerformLayout();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(args[0], $"setup-{scale * 100:0}.png"));
            foreach (var button in Children(form).OfType<Button>())
                if (button.Width < 80 || button.Height < 30) throw new Exception("Clipped: " + button.Text);
            Console.WriteLine($"PASS installer layout at {scale * 100:0}%");
        }
    }
    static IEnumerable<Control> Children(Control parent)
    {
        foreach (Control child in parent.Controls)
        { yield return child; foreach (var nested in Children(child)) yield return nested; }
    }
}
namespace RevitMCP.Setup
{
    static class Deployment
    {
        public const string Version = "0.0.2";
        public static string? FindRevit() => null;
        public static void Log(string text) => Console.Error.WriteLine(text);
        public static string ReadLicense() => "License";
        public static void Install(bool codex, bool claude, bool auto, string? path) => throw new NotSupportedException();
        public static void Uninstall() => throw new NotSupportedException();
    }
}
