using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QipcConverter
{
    public class DropPanel : Panel
    {
        public bool Highlight;
        public DropPanel() { DoubleBuffered = true; BackColor = Color.FromArgb(246, 249, 255); Padding = new Padding(18); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Highlight ? Color.FromArgb(43, 95, 232) : Color.FromArgb(183, 199, 225), Highlight ? 2 : 1))
            {
                pen.DashStyle = DashStyle.Dash;
                e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
            }
        }
    }

    public class MainForm : Form
    {
        private readonly List<InputFile> inputs = new List<InputFile>();
        private readonly HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly ListView list = new ListView();
        private readonly TextBox outputPath = new TextBox();
        private readonly TextBox log = new TextBox();
        private readonly Label queueTitle = new Label();
        private readonly Label stage = new Label();
        private readonly Label overallText = new Label();
        private readonly Label currentText = new Label();
        private readonly ProgressBar overall = new ProgressBar();
        private readonly ProgressBar current = new ProgressBar();
        private readonly Button start;
        private readonly Button stop;
        private readonly Button openOutput;
        private readonly List<Control> editControls = new List<Control>();
        private bool busy;
        private bool scanning;
        private volatile bool stopAfterCurrent;
        private readonly ToolTip tips = new ToolTip();
        private readonly Color muted = Color.FromArgb(91, 105, 127);
        private readonly Color accent = Color.FromArgb(43, 95, 232);

        public MainForm(string[] initialInputs)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "QIPC Converter";
            Font = new Font("맑은 고딕", 9F);
            ForeColor = Color.FromArgb(28, 42, 62);
            BackColor = Color.FromArgb(246, 248, 252);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1010, 850);
            MinimumSize = new Size(920, 800);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 20, 26, 18), ColumnCount = 1, RowCount = 8 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (float height in new[] { 72f, 126f, 38f }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (float height in new[] { 136f, 102f, 78f, 58f }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(root);

            var heading = new Panel { Dock = DockStyle.Fill };
            heading.Controls.Add(new Label { Text = "QIPC Converter", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), AutoSize = true, Location = new Point(0, 0) });
            heading.Controls.Add(new Label { Text = "BIN 녹화 파일을 원본 해상도의 MP4로", AutoSize = true, ForeColor = muted, Location = new Point(2, 45) });
            root.Controls.Add(heading, 0, 0);

            var inputDrop = new DropPanel { Name = "InputDrop", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10) };
            var inputGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            inputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 222));
            inputGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); inputGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            inputGrid.Controls.Add(new Label { Text = "01  입력 파일 또는 폴더를 여기에 놓으세요", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Anchor = AnchorStyles.Left }, 0, 0);
            inputGrid.Controls.Add(new Label { Text = "하위 폴더의 BIN 파일까지 자동으로 추가합니다.", AutoSize = true, ForeColor = muted, Anchor = AnchorStyles.Left }, 0, 1);
            var browseFiles = MakeButton("파일 찾기", false);
            var browseFolder = MakeButton("폴더 찾기", false);
            browseFiles.Width = browseFolder.Width = 102;
            var inputButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 16, 0, 0) };
            inputButtons.Controls.Add(browseFiles); inputButtons.Controls.Add(browseFolder);
            inputGrid.Controls.Add(inputButtons, 1, 0); inputGrid.SetRowSpan(inputButtons, 2);
            inputDrop.Controls.Add(inputGrid); root.Controls.Add(inputDrop, 0, 1);
            browseFiles.Click += async delegate
            {
                using (var dialog = new OpenFileDialog { Title = "변환할 BIN 파일 선택", Filter = "BIN 녹화 파일 (*.bin)|*.bin", Multiselect = true, CheckFileExists = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) await AddInputs(dialog.FileNames);
            };
            browseFolder.Click += async delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = "BIN 파일이 있는 폴더를 선택하세요. 하위 폴더도 포함합니다.", ShowNewFolderButton = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) await AddInputs(new[] { dialog.SelectedPath });
            };
            BindDrop(inputDrop, async delegate(string[] values) { await AddInputs(values); }, inputDrop);
            editControls.Add(browseFiles); editControls.Add(browseFolder);

            var queueBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            queueBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            queueBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); queueBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 238));
            queueTitle.Text = "변환 목록  0개"; queueTitle.Dock = DockStyle.Fill; queueTitle.TextAlign = ContentAlignment.MiddleLeft; queueTitle.Font = new Font(Font, FontStyle.Bold);
            var remove = MakeButton("선택 제거", false); var clear = MakeButton("목록 비우기", false);
            remove.Width = clear.Width = 108; remove.Height = clear.Height = 29;
            var queueButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
            queueButtons.Controls.Add(clear); queueButtons.Controls.Add(remove);
            queueBar.Controls.Add(queueTitle, 0, 0); queueBar.Controls.Add(queueButtons, 1, 0); root.Controls.Add(queueBar, 0, 2);
            remove.Click += delegate
            {
                foreach (ListViewItem row in list.SelectedItems.Cast<ListViewItem>().ToArray()) { var file = (InputFile)row.Tag; inputs.Remove(file); paths.Remove(file.Path); list.Items.Remove(row); }
                QueueChanged();
            };
            clear.Click += delegate { inputs.Clear(); paths.Clear(); list.Items.Clear(); QueueChanged(); };
            editControls.Add(remove); editControls.Add(clear);

            list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.GridLines = false;
            list.HideSelection = false; list.MultiSelect = true; list.BorderStyle = BorderStyle.FixedSingle; list.ShowItemToolTips = true;
            list.Columns.Add("입력 파일", 226); list.Columns.Add("수정한 날짜", 165); list.Columns.Add("저장 이름", 244); list.Columns.Add("상태", 240);
            list.Resize += delegate
            {
                int width = Math.Max(400, list.ClientSize.Width - 25);
                list.Columns[0].Width = (int)(width * .25); list.Columns[1].Width = (int)(width * .20);
                list.Columns[2].Width = (int)(width * .30); list.Columns[3].Width = width - list.Columns[0].Width - list.Columns[1].Width - list.Columns[2].Width;
            };
            list.Margin = new Padding(0, 0, 0, 14); list.AccessibleName = "변환할 BIN 파일 목록";
            BindDrop(list, async delegate(string[] values) { await AddInputs(values); }, null);
            root.Controls.Add(list, 0, 3);

            var outputDrop = new DropPanel { Name = "OutputDrop", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
            var outputGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(0) };
            outputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); outputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 119));
            outputGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); outputGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); outputGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outputGrid.Controls.Add(new Label { Text = "02  출력 폴더를 여기에 놓으세요", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
            outputGrid.SetColumnSpan(outputGrid.GetControlFromPosition(0, 0), 2);
            outputPath.Dock = DockStyle.Fill; outputPath.Margin = new Padding(0, 3, 10, 0); outputPath.AccessibleName = "출력 폴더 경로";
            outputGrid.Controls.Add(outputPath, 0, 1);
            var outputBrowse = MakeButton("폴더 찾기", false); outputBrowse.Dock = DockStyle.Fill; outputBrowse.Height = 30; outputBrowse.Margin = new Padding(0);
            outputGrid.Controls.Add(outputBrowse, 1, 1);
            var naming = new Label { Text = "이름: 수정 날짜_시간.mp4  ·  중복 시 _2, _3  ·  파일을 놓으면 상위 폴더 사용", AutoSize = true, ForeColor = muted, Margin = new Padding(0, 7, 0, 0) };
            outputGrid.Controls.Add(naming, 0, 2); outputGrid.SetColumnSpan(naming, 2);
            outputDrop.Controls.Add(outputGrid); root.Controls.Add(outputDrop, 0, 4);
            outputBrowse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = "MP4를 저장할 폴더를 선택하세요.", ShowNewFolderButton = true })
                {
                    if (Directory.Exists(outputPath.Text)) dialog.SelectedPath = outputPath.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) outputPath.Text = dialog.SelectedPath;
                }
            };
            BindDrop(outputDrop, delegate(string[] values)
            {
                try { outputPath.Text = Discovery.OutputFolder(values); }
                catch (Exception ex) { ShowError(ex.Message); }
            }, outputDrop);
            outputPath.TextChanged += delegate { if (!busy && !scanning) RefreshNames(); UpdateButtons(); };
            editControls.Add(outputPath); editControls.Add(outputBrowse);

            var progressGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Margin = new Padding(0) };
            progressGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); progressGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
            foreach (float h in new[] { 24f, 17f, 30f, 14f }) progressGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            stage.Text = "파일과 출력 폴더를 선택해 주세요."; stage.AutoEllipsis = true; stage.Dock = DockStyle.Fill; stage.Font = new Font(Font, FontStyle.Bold);
            overallText.Text = "전체  0%"; overallText.TextAlign = ContentAlignment.MiddleRight; overallText.Dock = DockStyle.Fill;
            overall.Dock = DockStyle.Fill; overall.Maximum = 1000; overall.AccessibleName = "전체 진행률";
            currentText.Text = "현재 파일  —"; currentText.Dock = DockStyle.Fill; currentText.ForeColor = muted; currentText.TextAlign = ContentAlignment.MiddleLeft;
            current.Dock = DockStyle.Fill; current.Maximum = 1000; current.AccessibleName = "현재 파일 진행률";
            progressGrid.Controls.Add(stage, 0, 0); progressGrid.Controls.Add(overallText, 1, 0);
            progressGrid.Controls.Add(overall, 0, 1); progressGrid.SetColumnSpan(overall, 2);
            progressGrid.Controls.Add(currentText, 0, 2); progressGrid.SetColumnSpan(currentText, 2);
            progressGrid.Controls.Add(current, 0, 3); progressGrid.SetColumnSpan(current, 2); root.Controls.Add(progressGrid, 0, 5);

            log.Dock = DockStyle.Fill; log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical;
            log.BorderStyle = BorderStyle.FixedSingle; log.BackColor = Color.White; log.ForeColor = muted;
            log.AccessibleName = "변환 기록"; log.Margin = new Padding(0, 0, 0, 8); root.Controls.Add(log, 0, 6);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 464));
            footer.Controls.Add(new Label { Text = "원본 해상도 · 음성 유지\n녹화 공백 제외 · 전체 영상 검사", Dock = DockStyle.Fill, ForeColor = muted, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) }, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            start = MakeButton("변환하기", true); start.Width = 126; start.Height = 42;
            stop = MakeButton("현재 파일 후 중지", false); stop.Width = 165; stop.Height = 42; stop.Enabled = false;
            openOutput = MakeButton("출력 폴더 열기", false); openOutput.Width = 148; openOutput.Height = 42;
            actions.Controls.Add(start); actions.Controls.Add(stop); actions.Controls.Add(openOutput);
            footer.Controls.Add(actions, 1, 0); root.Controls.Add(footer, 0, 7);
            start.Click += async delegate { await ConvertAll(); };
            stop.Click += delegate { stopAfterCurrent = true; stop.Enabled = false; stop.Text = "현재 파일 완료 대기"; AddLog("현재 파일의 검사·저장 후 나머지 작업을 중지합니다."); };
            openOutput.Click += delegate
            {
                try { if (Directory.Exists(outputPath.Text)) Process.Start(new ProcessStartInfo(outputPath.Text) { UseShellExecute = true }); }
                catch (Exception ex) { ShowError(ex.Message); }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!busy && !scanning) return;
                e.Cancel = true;
                MessageBox.Show(this, busy ? "현재 파일을 처리하고 있습니다. ‘현재 파일 후 중지’를 누른 뒤 작업이 끝나면 창을 닫아 주세요." : "파일 목록을 읽는 중입니다. 잠시 후 창을 닫아 주세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            Shown += async delegate { if (initialInputs.Length > 0) await AddInputs(initialInputs); };
            AddLog("BIN을 추가하고 저장할 폴더를 선택한 뒤 ‘변환하기’를 누르세요.");
            UpdateButtons();
            ResumeLayout(true);
        }

        private Button MakeButton(string text, bool primary)
        {
            var button = new Button { Text = text, Height = 35, Width = 108, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = primary ? accent : Color.White, ForeColor = primary ? Color.White : ForeColor, Margin = new Padding(4, 0, 0, 0), UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = primary ? accent : Color.FromArgb(205, 214, 228);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(31, 77, 197) : Color.FromArgb(234, 240, 251);
            return button;
        }
        private void BindDrop(Control control, Action<string[]> accept, DropPanel surface)
        {
            control.AllowDrop = true;
            control.DragEnter += delegate(object sender, DragEventArgs e)
            {
                e.Effect = !busy && !scanning && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                if (surface != null) { surface.Highlight = e.Effect != DragDropEffects.None; surface.Invalidate(); }
            };
            control.DragLeave += delegate { if (surface != null) { surface.Highlight = false; surface.Invalidate(); } };
            control.DragDrop += delegate(object sender, DragEventArgs e)
            {
                if (surface != null) { surface.Highlight = false; surface.Invalidate(); }
                if (busy || scanning || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                var values = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (values != null) accept(values);
            };
            foreach (Control child in control.Controls) BindDrop(child, accept, surface);
        }
        private async Task AddInputs(string[] values)
        {
            if (busy || scanning) return;
            scanning = true; UpdateButtons(); stage.Text = "폴더에서 BIN 파일을 찾는 중…";
            try
            {
                Discovery found = await Task.Run(() => Discovery.Find(values));
                int added = 0;
                foreach (InputFile file in found.Files)
                {
                    if (!paths.Add(file.Path)) continue;
                    inputs.Add(file); added++;
                    var row = new ListViewItem(new[] { Path.GetFileName(file.Path), file.ModifiedLocal.ToString("yyyy-MM-dd HH:mm:ss"), file.OutputStem + ".mp4", "대기" });
                    row.Tag = file; row.ToolTipText = file.Path; list.Items.Add(row);
                }
                foreach (string warning in found.Warnings) AddLog(warning);
                AddLog(added + "개 파일 추가 · 총 " + inputs.Count + "개");
                stage.Text = added == 0 ? "새로 추가할 BIN 파일이 없습니다." : "변환할 파일을 추가했습니다.";
                QueueChanged();
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { scanning = false; UpdateButtons(); }
        }
        private void QueueChanged() { queueTitle.Text = "변환 목록  " + inputs.Count + "개"; RefreshNames(); UpdateButtons(); }
        private void RefreshNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem row in list.Items)
            {
                var file = (InputFile)row.Tag;
                int n = 1; string candidate;
                do { candidate = file.OutputStem + (n == 1 ? "" : "_" + n) + ".mp4"; n++; }
                while (names.Contains(candidate) || OutputNameExists(candidate));
                names.Add(candidate); row.SubItems[2].Text = candidate;
            }
        }
        private bool OutputNameExists(string name)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(outputPath.Text)) return false;
                string candidate = Path.Combine(outputPath.Text.Trim().Trim('"'), name);
                return File.Exists(candidate) || Directory.Exists(candidate);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }
        private void UpdateButtons()
        {
            bool editable = !busy && !scanning;
            foreach (Control control in editControls) control.Enabled = editable;
            if (start != null) start.Enabled = editable && inputs.Count > 0 && !String.IsNullOrWhiteSpace(outputPath.Text);
            if (openOutput != null) openOutput.Enabled = Directory.Exists(outputPath.Text);
        }
        private void AddLog(string text) { log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine); }
        private void ShowError(string text) { AddLog(text); MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        private void Post(Action action) { if (!IsDisposed && IsHandleCreated) BeginInvoke(action); }

        private async Task ConvertAll()
        {
            if (busy || scanning || inputs.Count == 0) return;
            string folder;
            try { folder = Path.GetFullPath(outputPath.Text.Trim().Trim('"')); Directory.CreateDirectory(folder); }
            catch (Exception ex) { ShowError("출력 폴더를 사용할 수 없습니다.\n" + ex.Message); return; }
            outputPath.Text = folder;
            busy = true; stopAfterCurrent = false; stop.Text = "현재 파일 후 중지"; stop.Enabled = true; UpdateButtons();
            overall.Value = current.Value = 0; stage.Text = "변환 도구 준비 중…";
            InputFile[] batch = inputs.ToArray();
            int succeeded = 0, failed = 0, processed = 0;
            long totalBytes = Math.Max(1, batch.Sum(f => f.Length));
            long doneBytes = 0;
            for (int row = 0; row < list.Items.Count; row++) { list.Items[row].SubItems[3].Text = "대기"; list.Items[row].ForeColor = ForeColor; }
            var watch = Stopwatch.StartNew();
            try
            {
                await Task.Run(delegate
                {
                    using (var runtime = new OwnedDirectory(Path.GetTempPath()))
                    {
                        string ffmpeg = Ffmpeg.ExtractBundled(runtime.Path);
                        for (int i = 0; i < batch.Length; i++)
                        {
                            if (stopAfterCurrent) break;
                            InputFile file = batch[i]; int row = i;
                            var throttle = Stopwatch.StartNew(); string lastStage = "";
                            Post(delegate { list.Items[row].EnsureVisible(); list.Items[row].SubItems[3].Text = "변환 중"; });
                            try
                            {
                                ConversionResult result = Converter.Convert(file, folder, ffmpeg, delegate(ConversionProgress value)
                                {
                                    if (throttle.ElapsedMilliseconds < 100 && value.Stage == lastStage && value.Fraction < 1) return;
                                    throttle.Restart(); lastStage = value.Stage;
                                    double all = (doneBytes + file.Length * value.Fraction) / totalBytes;
                                    Post(delegate
                                    {
                                        current.Value = (int)(value.Fraction * 1000); overall.Value = Math.Min(1000, (int)(all * 1000));
                                        overallText.Text = String.Format("전체  {0:0}% · {1}/{2}", all * 100, row + 1, batch.Length);
                                        currentText.Text = String.Format("{0}  ·  {1:0}%  ·  {2}", Path.GetFileName(file.Path), value.Fraction * 100, value.Stage);
                                        stage.Text = value.Stage;
                                        list.Items[row].SubItems[3].Text = value.Stage + " " + (value.Fraction * 100).ToString("0") + "%";
                                    });
                                });
                                succeeded++;
                                Post(delegate
                                {
                                    list.Items[row].SubItems[2].Text = Path.GetFileName(result.Output);
                                    list.Items[row].SubItems[3].Text = "완료 · " + TimeSpan.FromSeconds(result.Info.Duration).ToString(@"hh\:mm\:ss");
                                    list.Items[row].ForeColor = Color.FromArgb(25, 125, 91);
                                    AddLog(Path.GetFileName(file.Path) + " → " + Path.GetFileName(result.Output) + " · " + result.ValidatedFrames.ToString("N0") + "프레임 검사 완료");
                                });
                            }
                            catch (Exception ex)
                            {
                                failed++; string message = ex.Message;
                                Post(delegate { list.Items[row].SubItems[3].Text = "실패 · 기록 확인"; list.Items[row].ForeColor = Color.FromArgb(180, 46, 63); list.Items[row].ToolTipText = file.Path + "\n" + message; AddLog(Path.GetFileName(file.Path) + " 실패: " + message); });
                            }
                            doneBytes += file.Length; processed++;
                        }
                    }
                });
                overall.Value = processed == batch.Length ? 1000 : Math.Min(1000, (int)(1000.0 * doneBytes / totalBytes));
                overallText.Text = String.Format("전체  {0:0}% · {1}/{2}", overall.Value / 10.0, processed, batch.Length);
                stage.Text = String.Format("{0} · 성공 {1}개 / 실패 {2}개{3}", processed == batch.Length ? "작업 완료" : "작업 중지", succeeded, failed, processed < batch.Length ? " / 대기 " + (batch.Length - processed) + "개" : "");
                currentText.Text = "소요 시간 " + watch.Elapsed.ToString(@"hh\:mm\:ss") + " · 출력 폴더에서 MP4를 확인하세요.";
                AddLog(stage.Text);
            }
            catch (Exception ex) { stage.Text = "작업을 시작하지 못했습니다."; ShowError(ex.Message); }
            finally { busy = false; stop.Enabled = false; stop.Text = "현재 파일 후 중지"; UpdateButtons(); }
        }
    }
}
