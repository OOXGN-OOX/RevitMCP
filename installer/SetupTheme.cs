namespace RevitMCP.Setup;

static class SetupTheme
{
    public static Color Surface => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(43, 43, 43);
    public static Color Text => SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(250, 249, 247);
    public static Color Field => SystemInformation.HighContrast ? SystemColors.Window : Color.Black;

    public static void Apply(Control control, Button? primary = null)
    {
        control.BackColor = Surface;
        control.ForeColor = Text;
        if (control is TextBox text)
        {
            text.BackColor = Field;
            text.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Text;
            text.BorderStyle = BorderStyle.FixedSingle;
        }
        if (control is CheckBox check) check.UseVisualStyleBackColor = false;
        if (control is Button button && !SystemInformation.HighContrast)
        {
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(156, 163, 175);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(75, 75, 75);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(107, 114, 128);
            button.Padding = new Padding(10, 4, 10, 4);
            if (ReferenceEquals(button, primary))
            {
                button.BackColor = Text;
                button.ForeColor = Color.Black;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(217, 217, 217);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(156, 163, 175);
            }
        }
        foreach (Control child in control.Controls) Apply(child, primary);
    }
}
