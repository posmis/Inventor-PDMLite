using System.Diagnostics;

namespace ShortenNames;

/// <summary>Окно: папка и ограничения → список «было → станет» (можно поправить) → «Переименовать» с предупреждением.</summary>
internal sealed class MainForm : Form
{
    private const string Caption = "Сокращение имён";

    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _maxPath = new() { Minimum = 100, Maximum = 259, Value = 200, Width = 70 };
    private readonly NumericUpDown _maxName = new() { Minimum = 30, Maximum = 200, Value = 100, Width = 70 };
    private readonly Button _browse = new() { Text = "Выбрать…", AutoSize = true };
    private readonly Button _find = new() { Text = "Найти длинные имена", AutoSize = true };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        BackgroundColor = SystemColors.Window,
    };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Width = 100, Visible = false };
    private readonly Button _rename = new() { Text = "Переименовать…", AutoSize = true, Enabled = false };
    private readonly Button _close = new() { Text = "Закрыть", AutoSize = true };
    private readonly Button _relink = new() { Text = "Перепривязать по журналу…", AutoSize = true };

    private List<NameChange> _changes = [];
    private bool _busy;

    public MainForm(string? folder)
    {
        Text = "Сокращение длинных имён файлов Inventor";
        Width = 1200;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(8) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "Папка проекта:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        top.Controls.Add(_folder, 1, 0);
        top.Controls.Add(_browse, 2, 0);

        var limits = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        limits.Controls.Add(new Label { Text = "Полный путь не длиннее:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        limits.Controls.Add(_maxPath);
        limits.Controls.Add(new Label { Text = "Имя файла не длиннее:", AutoSize = true, Margin = new Padding(16, 6, 4, 0) });
        limits.Controls.Add(_maxName);
        limits.Controls.Add(_find);
        top.Controls.Add(limits, 1, 1);
        top.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(1100, 0),
            Text = "Inventor не работает с путями длиннее 260 символов, а резервная копия в OldVersions добавляет к пути ещё около 20 — " +
                   "поэтому запас. Повторы в именах из STEP («A~B~B~C») убираются, остальное обрезается с кодом исходного имени. " +
                   "«Станет» можно поправить вручную. Ссылки перепривязываются во всех документах Inventor этой папки (с подпапками).",
        }, 1, 2);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 44, ColumnCount = 5, Padding = new Padding(8, 6, 8, 6) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++)
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(_progress, 1, 0);
        bottom.Controls.Add(_relink, 2, 0);
        bottom.Controls.Add(_rename, 3, 0);
        bottom.Controls.Add(_close, 4, 0);

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "", Width = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "From", HeaderText = "Было", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 40 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "To", HeaderText = "Станет (можно править)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Folder", HeaderText = "Папка", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 20 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Length", HeaderText = "Длина пути", ReadOnly = true, Width = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Reason", HeaderText = "Почему", ReadOnly = true, Width = 220 });
        _grid.CellEndEdit += (_, _) => UpdateLengths();
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Показать в проводнике Windows", null, (_, _) => ShowSelected());
        menu.Items.Add("Копировать имя «было»", null, (_, _) => CopySelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Включить выделенные", null, (_, _) => SetIncluded(true));
        menu.Items.Add("Исключить выделенные", null, (_, _) => SetIncluded(false));
        _grid.ContextMenuStrip = menu;
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && !_grid.Rows[e.RowIndex].Selected)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
            }
        };

        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(bottom);

        _browse.Click += (_, _) => Browse();
        _find.Click += (_, _) => Find();
        _rename.Click += (_, _) => Rename();
        _relink.Click += (_, _) => RelinkFromJournal();
        _close.Click += (_, _) => Close();
        AcceptButton = _find;

        _folder.Text = folder ?? "";
        _status.Text = "Выберите папку проекта и нажмите «Найти длинные имена».";
        Program.Log("Запуск");
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { Description = "Папка проекта — где искать длинные имена и перепривязывать ссылки", UseDescriptionForTitle = true };
        if (Directory.Exists(_folder.Text))
            dialog.InitialDirectory = _folder.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folder.Text = dialog.SelectedPath;
            Find();
        }
    }

    private string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(_folder.Text.Trim()));

    private void Find()
    {
        if (!Directory.Exists(_folder.Text.Trim()))
        {
            MessageBox.Show(this, "Выберите существующую папку.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var files = Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
        _changes = Shortener.Plan(Root, files, (int)_maxPath.Value, (int)_maxName.Value);
        _grid.Rows.Clear();
        foreach (var change in _changes)
        {
            var index = _grid.Rows.Add(change.To is not null, change.FromName, change.ToName ?? "(нельзя — сократите папку)",
                Path.GetRelativePath(Root, Path.GetDirectoryName(change.From)!), "", change.Reason);
            var row = _grid.Rows[index];
            row.Tag = change;
            row.Cells["From"].ToolTipText = change.From;
            if (change.To is null)
            {
                row.ReadOnly = true;
                row.DefaultCellStyle.ForeColor = Color.Firebrick;
            }
        }
        UpdateLengths();
        _rename.Enabled = _changes.Any(c => c.To is not null);
        _status.Text = _changes.Count == 0
            ? "Длинных имён нет."
            : $"Длинных имён: {_changes.Count}. Проверьте «Станет» и нажмите «Переименовать…».";
        Program.Log($"Поиск в {Root}: путь ≤ {_maxPath.Value}, имя ≤ {_maxName.Value}; найдено {_changes.Count}");
    }

    private void UpdateLengths()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not NameChange change)
                continue;
            var to = Target(row);
            row.Cells["Length"].Value = to is null ? $"{change.From.Length}" : $"{change.From.Length} → {to.Length}";
        }
    }

    /// <summary>Новый полный путь строки (с исправлением пользователя; расширение — исходное).</summary>
    private static string? Target(DataGridViewRow row)
    {
        if (row.Tag is not NameChange { To: not null } change)
            return null;
        var name = (row.Cells["To"].Value as string ?? "").Trim();
        if (name.Length == 0)
            return null;
        var extension = Path.GetExtension(change.From);
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name += extension;
        return Path.Combine(Path.GetDirectoryName(change.From)!, name);
    }

    private void Rename()
    {
        if (_busy)
            return;
        _grid.EndEdit();
        var selected = new List<NameChange>();
        var problems = new List<string>();
        var maxPath = (int)_maxPath.Value;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not NameChange { To: not null } change || row.Cells["Include"].Value is not true)
                continue;
            var to = Target(row);
            if (to is null)
            {
                problems.Add($"{change.FromName}: пустое новое имя.");
                continue;
            }
            var name = Path.GetFileName(to);
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                problems.Add($"{name}: недопустимые символы.");
            if (to.Length > maxPath)
                problems.Add($"{name}: путь всё ещё длинный ({to.Length} > {maxPath}).");
            if (!to.Equals(change.From, StringComparison.OrdinalIgnoreCase))
                selected.Add(change with { To = to });
        }
        foreach (var duplicate in selected.GroupBy(c => c.To!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"{Path.GetFileName(duplicate.Key)}: одно имя у нескольких файлов.");
        foreach (var change in selected.Where(c => File.Exists(c.To)))
            problems.Add($"{change.ToName}: такой файл уже есть.");
        if (problems.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, problems.Take(20)), Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Нечего переименовывать — отметьте файлы в списке.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var inventorRunning = Process.GetProcessesByName("Inventor").Length > 0;
        var warning =
            $"Будут переименованы файлы: {selected.Count}.{Environment.NewLine}" +
            $"Ссылки на них будут перепривязаны во всех документах Inventor папки:{Environment.NewLine}{Root}{Environment.NewLine}{Environment.NewLine}" +
            (inventorRunning
                ? "ВНИМАНИЕ: Inventor запущен. Закройте в нём все документы из этой папки (лучше — сам Inventor), иначе он перезапишет файлы старыми ссылками." + Environment.NewLine + Environment.NewLine
                : "") +
            "Документы, которые изменятся, сначала копируются (журнал и копии — в %LOCALAPPDATA%\\PdmLite\\shorten-names); " +
            "при ошибке всё возвращается. Документы из других папок, которые ссылаются на эти файлы, НЕ обновятся — выберите папку проекта целиком." +
            Environment.NewLine + Environment.NewLine + "Переименовать?";
        if (MessageBox.Show(this, warning, Caption, MessageBoxButtons.YesNo, inventorRunning ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        Run(selected, relinkOnly: false);
    }

    /// <summary>
    /// В фоне (Apprentice — в однопоточном апартаменте): чтение ссылок → подтверждение со списком документов →
    /// переименование (или только перепривязка).
    /// </summary>
    private void Run(List<NameChange> changes, bool relinkOnly)
    {
        SetBusy(true);
        var root = Root;
        Program.Log($"{(relinkOnly ? "Перепривязка по журналу" : "Переименование")}: {changes.Count} файлов в {root}");
        var thread = new Thread(() =>
        {
            RelinkResult? result = null;
            Exception? error = null;
            var cancelled = false;
            try
            {
                using var links = ApprenticeLinks.Start(Program.Log);
                var relinker = new Relinker(links, Report);
                var plan = relinker.Prepare(root, changes);
                if ((bool)Invoke(() => ConfirmPlan(plan, changes.Count, relinkOnly)))
                    result = relinker.Execute(root, changes, plan, relinkOnly);
                else
                    cancelled = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
            BeginInvoke(() => Finished(result, error, cancelled, relinkOnly));
        });
        thread.SetApartmentState(ApartmentState.STA); // Apprentice — COM в однопоточном апартаменте
        thread.IsBackground = true;
        thread.Start();
    }

    /// <summary>Второе подтверждение: где будут заменены ссылки и что настораживает.</summary>
    private bool ConfirmPlan(RelinkPlan plan, int files, bool relinkOnly)
    {
        static string List(IEnumerable<string> items) =>
            string.Join(Environment.NewLine, items.Take(15).Select(i => "  " + i)) + (items.Count() > 15 ? $"{Environment.NewLine}  … и ещё {items.Count() - 15}" : "");

        if (plan.Unreadable.Count > 0)
        {
            MessageBox.Show(this,
                "Ссылки этих документов не прочитаны — они могут ссылаться на переименовываемые файлы, поэтому ничего не изменено:"
                + Environment.NewLine + List(plan.Unreadable.Select(Path.GetFileName)!) + Environment.NewLine + Environment.NewLine
                + "Закройте Inventor и повторите. Подробности — в журнале: " + Program.LogPath,
                Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        var text = $"Ссылки будут заменены в документах ({plan.Parents.Count}):" + Environment.NewLine
                   + (plan.Parents.Count == 0 ? "  (ни один документ папки не ссылается на эти файлы)" : List(plan.Parents.Select(p => Path.GetRelativePath(Root, p))))
                   + Environment.NewLine;
        if (plan.Unreferenced.Count > 0)
            text += Environment.NewLine + $"На эти файлы ссылок в папке НЕ найдено ({plan.Unreferenced.Count}) — если их используют сборки " +
                    "из другой папки, выберите папку выше:" + Environment.NewLine + List(plan.Unreferenced.Select(Path.GetFileName)!) + Environment.NewLine;
        text += Environment.NewLine + (relinkOnly ? "Перепривязать ссылки?" : $"Переименовать файлы ({files}) и перепривязать ссылки?");
        return MessageBox.Show(this, text, Caption, MessageBoxButtons.YesNo,
            plan.Unreferenced.Count > 0 || plan.Parents.Count == 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    /// <summary>Файлы уже переименованы прошлым запуском, а ссылки не перепривязались — перепривязать по его журналу.</summary>
    private void RelinkFromJournal()
    {
        if (_busy)
            return;
        if (!Directory.Exists(_folder.Text.Trim()))
        {
            MessageBox.Show(this, "Сначала выберите папку проекта — в ней будут заменены ссылки.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new OpenFileDialog
        {
            Title = "Журнал прошлого переименования (_журнал.txt или _переименования.json)",
            Filter = "Журнал|_журнал.txt;_переименования.json|Все файлы|*.*",
            InitialDirectory = Directory.Exists(Relinker.DefaultBackupRoot) ? Relinker.DefaultBackupRoot : null,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        List<NameChange> changes;
        try
        {
            changes = Relinker.LoadChanges(dialog.FileName).Where(c => c.To is not null && File.Exists(c.To)).ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Журнал не прочитан: " + ex.Message, Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (changes.Count == 0)
        {
            MessageBox.Show(this, "В журнале нет переименованных файлов, которые сейчас есть на диске.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _grid.Rows.Clear();
        foreach (var change in changes)
        {
            var row = _grid.Rows[_grid.Rows.Add(true, change.FromName, change.ToName, Path.GetRelativePath(Root, Path.GetDirectoryName(change.From)!), "", "по журналу")];
            row.Tag = change;
            row.ReadOnly = true;
        }
        _changes = changes;
        _rename.Enabled = false;
        _status.Text = $"По журналу: {changes.Count} переименованных файлов. Ссылки на старые имена будут заменены на новые.";
        Run(changes, relinkOnly: true);
    }

    private void Report(string text)
    {
        Program.Log(text);
        BeginInvoke(() => _status.Text = text);
    }

    private void Finished(RelinkResult? result, Exception? error, bool cancelled, bool relinkOnly)
    {
        SetBusy(false);
        if (cancelled)
        {
            _status.Text = "Отменено — ничего не изменено.";
            return;
        }
        if (error is not null)
        {
            Program.Log("ОШИБКА: " + error);
            MessageBox.Show(this, error.Message + Environment.NewLine + Environment.NewLine + "Журнал: " + Program.LogPath, Caption,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _status.Text = "Не выполнено.";
            return;
        }
        var text = (relinkOnly ? "" : $"Переименовано файлов: {result!.Renamed}. ")
                   + $"Ссылки заменены: {result!.References} в документах: {result.Documents}." +
                   (result.Warnings.Count == 0 ? "" : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, result.Warnings.Take(20))) +
                   Environment.NewLine + Environment.NewLine + "Журнал и копии: " + result.BackupFolder;
        Program.Log(text);
        MessageBox.Show(this, text, Caption, MessageBoxButtons.OK, result.Warnings.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        Find();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _progress.Visible = busy;
        _rename.Enabled = !busy;
        _find.Enabled = !busy;
        _browse.Enabled = !busy;
        _relink.Enabled = !busy;
        _grid.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private IEnumerable<NameChange> Selected() =>
        _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<NameChange>();

    private void ShowSelected()
    {
        if (Selected().FirstOrDefault() is { } change)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{change.From}\"") { UseShellExecute = true })?.Dispose();
    }

    private void CopySelected()
    {
        var text = string.Join(Environment.NewLine, Selected().Select(c => c.FromName));
        if (text.Length > 0)
            Clipboard.SetText(text);
    }

    private void SetIncluded(bool value)
    {
        foreach (DataGridViewRow row in _grid.SelectedRows)
            if (row.Tag is NameChange { To: not null })
                row.Cells["Include"].Value = value;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Идёт переименование — дождитесь окончания.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        base.OnFormClosing(e);
    }
}
