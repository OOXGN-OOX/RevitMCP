namespace RevitMCP.Setup;

sealed class SetupWindow : Form
{
    readonly CheckBox codex = new() { Text = "Подключить Codex", AutoSize = true };
    readonly CheckBox claude = new() { Text = "Подключить Claude Desktop", AutoSize = true };
    readonly CheckBox auto = new() { Text = "Включить автоподтверждение команд Revit", AutoSize = true };
    readonly TextBox revit = new() { Dock = DockStyle.Fill };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(570, 0) };
    readonly Button action = new() { Text = "Установить", AutoSize = true, MinimumSize = new Size(120, 38) };

    public SetupWindow(bool uninstall)
    {
        Text = "Revit MCP — " + (uninstall ? "удаление" : "установка");
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(650, 580);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Font = new Font("Segoe UI", 10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(28, 24, 28, 16),
            ColumnCount = 1, RowCount = 12, GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void AddRow(Control control)
        {
            control.Margin = new Padding(0, 0, 0, 14);
            layout.Controls.Add(control);
        }
        using var headerStream = typeof(SetupWindow).Assembly.GetManifestResourceStream("RevitMCP.Product.png")!;
        using var headerImage = Image.FromStream(headerStream);
        var header = new PictureBox { Image = new Bitmap(headerImage), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(570, 114), AccessibleName = "Revit MCP · OOXGN" };
        header.Disposed += (_, _) => header.Image?.Dispose();
        AddRow(header);
        AddRow(new Label { Text = "Версия " + Deployment.Version, AutoSize = true });
        AddRow(new Label { AutoSize = true, MaximumSize = new Size(570, 0), Text = uninstall
            ? "Удаление надстройки и её MCP-подключения. Модели, снимки и настройки сохранятся. Закройте Revit и MCP-клиент."
            : "Локальный MCP-мост для Autodesk Revit 2026 x64. Установка для текущего пользователя; Node.js и зависимости включены." });
        AddRow(new Label { Text = "Каталог Revit 2026:", AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        revit.Text = Deployment.FindRevit() ?? @"C:\Program Files\Autodesk\Revit 2026";
        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = 40
        };
        pathRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        revit.Dock = DockStyle.None;
        revit.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        revit.Margin = Padding.Empty;
        var browse = new Button { Text = "Обзор…", Dock = DockStyle.Fill, Margin = new Padding(10, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { InitialDirectory = revit.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) revit.Text = dialog.SelectedPath;
        };
        pathRow.Controls.Add(revit, 0, 0);
        pathRow.Controls.Add(browse, 1, 0);
        AddRow(pathRow);
        codex.Checked = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        claude.Checked = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude"));
        AddRow(codex); AddRow(claude); AddRow(auto);

        AddRow(status);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(28, 12, 28, 20), WrapContents = false
        };
        var close = new Button { Text = "Закрыть", AutoSize = true, MinimumSize = new Size(110, 38), DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        var license = new Button { Text = "Лицензия", AutoSize = true, MinimumSize = new Size(110, 38) };
        license.Click += (_, _) =>
        {
            using var reader = new Form { Text = "GNU AGPL v3", Size = new Size(750, 650), StartPosition = FormStartPosition.CenterParent };
            reader.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill, Font = Font, Text = Deployment.ReadLicense() });
            SetupTheme.Apply(reader);
            reader.ShowDialog(this);
        };
        buttons.Controls.Add(close); buttons.Controls.Add(action); buttons.Controls.Add(license);
        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        content.Controls.Add(layout);
        Controls.Add(content); Controls.Add(buttons);
        AcceptButton = action;
        CancelButton = close;
        if (uninstall)
        {
            revit.Enabled = browse.Enabled = codex.Enabled = claude.Enabled = auto.Enabled = false;
            action.Text = "Удалить";
        }
        action.Click += (_, _) =>
        {
            action.Enabled = false;
            status.Text = "Выполняется…";
            Refresh();
            try
            {
                if (uninstall) Deployment.Uninstall();
                else Deployment.Install(codex.Checked, claude.Checked, auto.Checked, revit.Text);
                status.Text = uninstall ? "Надстройка удалена. Данные сохранены."
                    : "Готово. Запустите Revit 2026 и перезапустите MCP-клиент.";
            }
            catch (Exception ex)
            {
                status.Text = "Ошибка: " + ex.Message;
                action.Enabled = true;
                Deployment.Log("ERROR: " + ex.Message);
            }
        };
        SetupTheme.Apply(this, action);
    }
}
