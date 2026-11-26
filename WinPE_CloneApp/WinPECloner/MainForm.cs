using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WinPECloner
{
    public class MainForm : Form
    {
        private ComboBox cmbSource = null!;
        private DataGridView gridDisks = null!;
        private Button btnRefresh = null!;
        private Button btnWipe = null!;
        private Button btnClone = null!;
        private Button btnCancel = null!;
        private CheckBox chkAutoShutdown = null!;
        private CheckBox chkParallelMode = null!;
        private ProgressBar progressBar = null!;
        private Label lblProgressStatus = null!;
        private TextBox txtLog = null!;
        private Label lblHeader = null!;
        private ContextMenuStrip contextMenuDisk = null!;
        private ToolStripMenuItem menuItemConvertToGpt = null!;

        private List<DiskInfo> currentDisks = new();
        private CancellationTokenSource? cts;
        private bool isOperationRunning = false;
        private bool _isInitializing = true;

        public MainForm()
        {
            _isInitializing = true;
            InitializeComponent();
            RefreshDiskList();
            _isInitializing = false;
            ApplySourceProtection();
        }

        private void InitializeComponent()
        {
            this.Text = "TessyClone";
            this.Size = new Size(1024, 720);
            this.MinimumSize = new Size(800, 560);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.WindowState = FormWindowState.Maximized;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            this.BackColor = Color.FromArgb(245, 247, 250);

            // 1. Barra Superiore Intestazione ("TessyClone")
            var pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Color.FromArgb(24, 32, 47),
                Padding = new Padding(16, 8, 16, 8)
            };

            lblHeader = new Label
            {
                Text = "⚡ TessyClone",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 12)
            };

            btnRefresh = new Button
            {
                Text = "↻ Aggiorna",
                Size = new Size(135, 32),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(pnlHeader.Width - 151, 10),
                BackColor = Color.FromArgb(41, 128, 185),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.Click += (s, e) => RefreshDiskList();

            pnlHeader.Controls.Add(lblHeader);
            pnlHeader.Controls.Add(btnRefresh);

            // 2. Pannello Selezione Sorgente (Una Tantum)
            var pnlSource = new Panel
            {
                Dock = DockStyle.Top,
                Height = 48,
                BackColor = Color.White,
                Padding = new Padding(16, 8, 16, 8)
            };

            var lblSrcTitle = new Label
            {
                Text = "Disco SORGENTE (Master):",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(44, 62, 80),
                Location = new Point(16, 12),
                AutoSize = true
            };

            cmbSource = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9.5f),
                Location = new Point(210, 9),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Width = Math.Max(300, pnlSource.ClientSize.Width - 230)
            };
            cmbSource.SelectedIndexChanged += CmbSource_SelectedIndexChanged;

            pnlSource.Controls.Add(lblSrcTitle);
            pnlSource.Controls.Add(cmbSource);

            // 3. Menu Contestuale per Tasto Destro -> "Converti in GPT"
            contextMenuDisk = new ContextMenuStrip();
            menuItemConvertToGpt = new ToolStripMenuItem("Converti in GPT (Cancella ed Inizializza)");
            menuItemConvertToGpt.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            menuItemConvertToGpt.Click += MenuItemConvertToGpt_Click;
            contextMenuDisk.Items.Add(menuItemConvertToGpt);

            // 4. Griglia Elenco Dischi (Destinazioni) con AutoSizeColumnsMode.Fill
            gridDisks = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowTemplate = { Height = 38 }
            };

            var colCheck = new DataGridViewCheckBoxColumn
            {
                Name = "ColDest",
                HeaderText = "Seleziona",
                FillWeight = 8,
                MinimumWidth = 65,
                ReadOnly = true, // ReadOnly impedisce l'attivazione della modifica nativa ed elimina il bug del doppio clic
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            var colDiskNum = new DataGridViewTextBoxColumn
            {
                Name = "ColNum",
                HeaderText = "Unità",
                FillWeight = 8,
                MinimumWidth = 55,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            var colModel = new DataGridViewTextBoxColumn
            {
                Name = "ColModel",
                HeaderText = "Modello Hardware",
                FillWeight = 22,
                MinimumWidth = 110,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            var colBus = new DataGridViewTextBoxColumn
            {
                Name = "ColBus",
                HeaderText = "Bus",
                FillWeight = 7,
                MinimumWidth = 50,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            var colStyle = new DataGridViewTextBoxColumn
            {
                Name = "ColStyle",
                HeaderText = "Tabella",
                FillWeight = 7,
                MinimumWidth = 50,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            var colCap = new DataGridViewTextBoxColumn
            {
                Name = "ColCap",
                HeaderText = "Capacità",
                FillWeight = 10,
                MinimumWidth = 65,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            var colPart = new DataGridViewTextBoxColumn
            {
                Name = "ColPart",
                HeaderText = "Partizioni & Volumi Attuali (Occupazione)",
                FillWeight = 20,
                MinimumWidth = 110,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            var colStatus = new DataGridViewTextBoxColumn
            {
                Name = "ColStatus",
                HeaderText = "Stato Operativo",
                FillWeight = 22,
                MinimumWidth = 145,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };

            gridDisks.Columns.AddRange(colCheck, colDiskNum, colModel, colBus, colStyle, colCap, colPart, colStatus);
            gridDisks.ContextMenuStrip = contextMenuDisk;

            // Gestione DataError per impedire qualsiasi crash da mismatch tipi di cella
            gridDisks.DataError += (s, e) =>
            {
                e.ThrowException = false;
                e.Cancel = false;
            };

            // Eventi griglia: cell painting, selezione mirata e click checkbox
            gridDisks.CellPainting += GridDisks_CellPainting;
            gridDisks.CellContentClick += GridDisks_CellContentClick;
            gridDisks.CellMouseDown += GridDisks_CellMouseDown;
            gridDisks.SelectionChanged += GridDisks_SelectionChanged;
            gridDisks.CellFormatting += GridDisks_CellFormatting;

            // 5. Barra Azioni Inferiore (Layout Flow compatto responsive)
            var pnlActions = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 54,
                BackColor = Color.White,
                Padding = new Padding(12, 8, 12, 8)
            };

            var flowActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = false,
                BackColor = Color.Transparent
            };

            btnWipe = new Button
            {
                Text = "🗑️ Pulisci Disco",
                Size = new Size(150, 36),
                BackColor = Color.FromArgb(192, 57, 43),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 10, 0)
            };
            btnWipe.FlatAppearance.BorderSize = 0;
            btnWipe.Click += BtnWipe_Click;

            btnClone = new Button
            {
                Text = "⚡ Clona su Selezionati",
                Size = new Size(210, 36),
                BackColor = Color.FromArgb(39, 174, 96),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 10, 0)
            };
            btnClone.FlatAppearance.BorderSize = 0;
            btnClone.Click += BtnClone_Click;

            btnCancel = new Button
            {
                Text = "⏹ Annulla",
                Size = new Size(95, 36),
                BackColor = Color.FromArgb(127, 140, 141),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Enabled = false,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 15, 0)
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Click += (s, e) => cts?.Cancel();

            chkAutoShutdown = new CheckBox
            {
                Text = "Spegni PC al termine",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(44, 62, 80),
                AutoSize = true,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 7, 15, 0)
            };

            chkParallelMode = new CheckBox
            {
                Text = "Modalità Parallela (max 2 dischi)",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(44, 62, 80),
                AutoSize = true,
                Cursor = Cursors.Hand,
                Checked = false,
                Margin = new Padding(0, 7, 0, 0)
            };

            flowActions.Controls.Add(btnWipe);
            flowActions.Controls.Add(btnClone);
            flowActions.Controls.Add(btnCancel);
            flowActions.Controls.Add(chkAutoShutdown);
            flowActions.Controls.Add(chkParallelMode);

            pnlActions.Controls.Add(flowActions);

            // 6. Pannello Avanzamento e Registro Console
            var pnlBottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 175,
                BackColor = Color.FromArgb(245, 247, 250),
                Padding = new Padding(12, 6, 12, 8)
            };

            progressBar = new ProgressBar
            {
                Dock = DockStyle.Top,
                Height = 22,
                Minimum = 0,
                Maximum = 1000
            };

            lblProgressStatus = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Text = "Pronto. Seleziona il disco master sorgente e spunta le destinazioni.",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 73, 94),
                TextAlign = ContentAlignment.MiddleLeft
            };

            txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(20, 24, 33),
                ForeColor = Color.FromArgb(46, 204, 113),
                Font = new Font("Consolas", 9f)
            };

            pnlBottom.Controls.Add(txtLog);
            pnlBottom.Controls.Add(lblProgressStatus);
            pnlBottom.Controls.Add(progressBar);

            // Aggiungi tutti i controlli al Form
            this.Controls.Add(gridDisks);
            this.Controls.Add(pnlActions);
            this.Controls.Add(pnlBottom);
            this.Controls.Add(pnlSource);
            this.Controls.Add(pnlHeader);
        }

        private void AppendLog(string message)
        {
            if (txtLog.InvokeRequired)
            {
                txtLog.BeginInvoke(new Action(() => AppendLog(message)));
                return;
            }
            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        }

        private void RefreshDiskList(int? preferredSourceDiskNumber = null, ISet<int>? checkedDiskNumbersToKeep = null, bool force = false)
        {
            if (isOperationRunning && !force) return;

            int targetSourceDiskNum = preferredSourceDiskNumber ?? GetCurrentSourceDiskNumber();

            var targetCheckedNums = checkedDiskNumbersToKeep;
            if (targetCheckedNums == null)
            {
                targetCheckedNums = new HashSet<int>();
                int colCheck = gridDisks.Columns["ColDest"]?.Index ?? -1;
                if (colCheck >= 0)
                {
                    foreach (DataGridViewRow row in gridDisks.Rows)
                    {
                        if (row.Tag is DiskInfo disk && row.Cells[colCheck] is DataGridViewCheckBoxCell cell && cell.Value is bool b && b)
                        {
                            targetCheckedNums.Add(disk.DiskNumber);
                        }
                    }
                }
            }

            bool wasInit = _isInitializing;
            _isInitializing = true;
            try
            {
                AppendLog("Rilevamento e sincronizzazione dischi fisici collegati...");
                // Rescan rapido bus per far riapparire eventuali dischi NVMe disallineati
                DiskManager.RescanBus(AppendLog);
                currentDisks = DiskManager.EnumerateDisks();

                gridDisks.Rows.Clear();
                cmbSource.Items.Clear();

                int sourceIndexToSelect = -1;

                for (int i = 0; i < currentDisks.Count; i++)
                {
                    var d = currentDisks[i];
                    cmbSource.Items.Add(d.DisplayName);

                    if (d.DiskNumber == targetSourceDiskNum)
                    {
                        sourceIndexToSelect = i;
                    }

                    bool shouldCheck = (d.DiskNumber != targetSourceDiskNum) && targetCheckedNums.Contains(d.DiskNumber);

                    int rowIndex = gridDisks.Rows.Add(
                        shouldCheck,
                        $"Disco {d.DiskNumber}",
                        d.Model,
                        d.BusType,
                        d.PartitionStyleName,
                        d.CleanCapacity,
                        d.VolumeSummary,
                        "Disponibile"
                    );
                    gridDisks.Rows[rowIndex].Tag = d;
                }

                if (sourceIndexToSelect >= 0)
                {
                    cmbSource.SelectedIndex = sourceIndexToSelect;
                }
                else if (cmbSource.Items.Count > 0)
                {
                    cmbSource.SelectedIndex = 0;
                }

                AppendLog($"Rilevati {currentDisks.Count} dischi fisici (>= 100 GB).");
            }
            finally
            {
                _isInitializing = wasInit;
            }

            if (!_isInitializing)
            {
                ApplySourceProtection();
            }
        }

        private int GetCurrentSourceDiskNumber()
        {
            int idx = cmbSource.SelectedIndex;
            if (idx >= 0 && idx < currentDisks.Count)
            {
                return currentDisks[idx].DiskNumber;
            }
            return -1;
        }

        private void CmbSource_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_isInitializing) return;
            ApplySourceProtection();
        }

        private void ApplySourceProtection()
        {
            int srcDiskNum = GetCurrentSourceDiskNumber();
            if (srcDiskNum < 0) return;

            int colCheckIdx = gridDisks.Columns["ColDest"].Index;

            for (int rowIndex = 0; rowIndex < gridDisks.Rows.Count; rowIndex++)
            {
                var row = gridDisks.Rows[rowIndex];
                if (row.Tag is not DiskInfo disk) continue;

                if (disk.DiskNumber == srcDiskNum)
                {
                    row.Cells[colCheckIdx].Value = false;
                    row.DefaultCellStyle.BackColor = Color.FromArgb(235, 245, 255);
                    row.Cells["ColStatus"].Value = "SORGENTE (Master - Protetto)";
                }
                else
                {
                    row.DefaultCellStyle.BackColor = Color.White;
                    if (Convert.ToString(row.Cells["ColStatus"].Value) == "SORGENTE (Master - Protetto)")
                    {
                        row.Cells["ColStatus"].Value = "Disponibile";
                    }
                }
            }

            // Invalida l'intera griglia per forzare il repaint immediato (checkbox, barra partizioni e volumi)
            gridDisks.Invalidate();
            gridDisks.ClearSelection();
            UpdateWipeButtonText();
        }

        private void UpdateWipeButtonText()
        {
            var targets = GetSelectedTargetDisks();
            if (targets.Count >= 2)
            {
                btnWipe.Text = "🗑️ Pulisci Dischi";
            }
            else
            {
                btnWipe.Text = "🗑️ Pulisci Disco";
            }
        }

        private List<DiskInfo> GetSelectedTargetDisks()
        {
            var targets = new List<DiskInfo>();
            int colCheckIdx = gridDisks.Columns["ColDest"].Index;
            int srcDiskNum = GetCurrentSourceDiskNumber();

            foreach (DataGridViewRow row in gridDisks.Rows)
            {
                if (row.Tag is DiskInfo disk && disk.DiskNumber != srcDiskNum)
                {
                    if (row.Cells[colCheckIdx] is DataGridViewCheckBoxCell cell && cell.Value is bool b && b)
                    {
                        targets.Add(disk);
                    }
                }
            }
            return targets;
        }

        // 4. Custom Painting: Rimozione visiva checkbox Sorgente + Barra di riempimento
        private void GridDisks_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            int colCheckIdx = gridDisks.Columns["ColDest"].Index;

            // 1. Rimozione visiva definitiva della checkbox sul disco Sorgente (Master)
            if (e.RowIndex >= 0 && e.ColumnIndex == colCheckIdx && e.Graphics != null)
            {
                if (gridDisks.Rows[e.RowIndex].Tag is DiskInfo disk && disk.DiskNumber == GetCurrentSourceDiskNumber())
                {
                    e.PaintBackground(e.ClipBounds, false);
                    e.Handled = true; // Impedisce il disegno della checkbox
                    return;
                }
            }

            // 2. Disegno custom della barra di riempimento spazio
            if (e.RowIndex >= 0 && e.ColumnIndex == gridDisks.Columns["ColPart"].Index && e.Graphics != null)
            {
                e.PaintBackground(e.CellBounds, true);

                if (gridDisks.Rows[e.RowIndex].Tag is DiskInfo disk && disk.TotalSizeBytes > 0)
                {
                    double ratio = disk.FillRatio;

                    int barHeight = 6;
                    int barWidth = Math.Max(0, e.CellBounds.Width - 16);
                    int barX = e.CellBounds.X + 8;
                    int barY = e.CellBounds.Bottom - barHeight - 4;

                    using (var bgBrush = new SolidBrush(Color.FromArgb(226, 232, 240)))
                    {
                        e.Graphics.FillRectangle(bgBrush, barX, barY, barWidth, barHeight);
                    }

                    int fillWidth = (int)(barWidth * ratio);
                    if (fillWidth > 0)
                    {
                        Color fillColor = ratio > 0.90 ? Color.FromArgb(239, 68, 68)
                                        : ratio > 0.75 ? Color.FromArgb(245, 158, 11)
                                        : Color.FromArgb(16, 185, 129);

                        using (var fillBrush = new SolidBrush(fillColor))
                        {
                            e.Graphics.FillRectangle(fillBrush, barX, barY, fillWidth, barHeight);
                        }
                    }
                    else if (disk.PhysicalPartitionsAllocatedBytes > 0 && barWidth > 0)
                    {
                        // Disco partizionato senza lettera / spazio usato non leggibile: barra neutra sottile (mai rossa 100%)
                        using (var neutralBrush = new SolidBrush(Color.FromArgb(148, 163, 184)))
                        {
                            e.Graphics.FillRectangle(neutralBrush, barX, barY + barHeight - 2, barWidth, 2);
                        }
                    }
                }

                // Disegna il testo della cella lasciando spazio sopra la barra
                var textBounds = new Rectangle(e.CellBounds.X, e.CellBounds.Y, e.CellBounds.Width, Math.Max(0, e.CellBounds.Height - 8));
                var font = e.CellStyle?.Font ?? this.Font;
                var foreColor = e.CellStyle?.ForeColor ?? Color.Black;
                TextRenderer.DrawText(e.Graphics, Convert.ToString(e.Value) ?? string.Empty, font, textBounds,
                    foreColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

                e.Handled = true;
            }
        }

        // Click singolo immediato sulla checkbox (ReadOnly = true elimina conflitti di editing nativo)
        private void GridDisks_CellContentClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            int colCheckIdx = gridDisks.Columns["ColDest"].Index;

            if (e.ColumnIndex == colCheckIdx)
            {
                var row = gridDisks.Rows[e.RowIndex];
                if (row.Tag is DiskInfo disk)
                {
                    // Ignora clic sul disco sorgente
                    if (disk.DiskNumber == GetCurrentSourceDiskNumber())
                    {
                        row.Cells[colCheckIdx].Value = false;
                        return;
                    }

                    if (row.Cells[colCheckIdx] is DataGridViewCheckBoxCell cell)
                    {
                        bool cur = cell.Value is bool b && b;
                        cell.Value = !cur;
                        gridDisks.ClearSelection();
                        UpdateWipeButtonText();
                    }
                }
            }
        }

        // Gestione selettiva selezione riga:
        // - Clic su checkbox o altre celle: nessuna evidenziazione riga
        // - Clic su "Modello Hardware": evidenzia la riga
        // - Clic destro su qualsiasi colonna: evidenzia la riga e attiva il menu GPT
        private void GridDisks_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0) return;

            // Caso 1: Tasto destro su qualunque colonna -> seleziona la riga e attiva il menu GPT
            if (e.Button == MouseButtons.Right)
            {
                gridDisks.ClearSelection();
                gridDisks.Rows[e.RowIndex].Selected = true;

                if (gridDisks.Rows[e.RowIndex].Tag is DiskInfo disk)
                {
                    menuItemConvertToGpt.Enabled = (disk.PartitionStyle == NativeMethods.PARTITION_STYLE.PARTITION_STYLE_MBR);
                    menuItemConvertToGpt.Text = $"Converti Disco {disk.DiskNumber} in GPT";
                }
                return;
            }

            // Caso 2: Clic sinistro
            if (e.Button == MouseButtons.Left)
            {
                int colModelIdx = gridDisks.Columns["ColModel"].Index;

                if (e.ColumnIndex == colModelIdx)
                {
                    // Clic su "Modello Hardware" -> unica colonna che evidenzia la riga in blu
                    gridDisks.ClearSelection();
                    gridDisks.Rows[e.RowIndex].Selected = true;
                }
                else
                {
                    // Tutte le altre celle (inclusa la checkbox) -> deseleziona
                    gridDisks.BeginInvoke(new Action(() => gridDisks.ClearSelection()));
                }
            }
        }

        private void GridDisks_SelectionChanged(object? sender, EventArgs e)
        {
            if (gridDisks.SelectedRows.Count > 0)
            {
                int colModelIdx = gridDisks.Columns["ColModel"].Index;
                if (gridDisks.CurrentCell != null && gridDisks.CurrentCell.ColumnIndex != colModelIdx && Control.MouseButtons != MouseButtons.Right)
                {
                    gridDisks.ClearSelection();
                }
            }
        }

        private async void MenuItemConvertToGpt_Click(object? sender, EventArgs e)
        {
            if (gridDisks.SelectedRows.Count == 0) return;
            if (gridDisks.SelectedRows[0].Tag is not DiskInfo disk) return;

            var confirm = MessageBox.Show(
                $"ATTENZIONE: Stai per convertire il Disco {disk.DiskNumber} ({disk.Model}) in GPT.\n\nQuesta operazione eseguirà la pulizia (CLEAN) della tabella partizioni ed eliminerà tutti i volumi presenti.\n\nVuoi procedere?",
                "Conferma Conversione GPT", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes) return;

            int srcNum = GetCurrentSourceDiskNumber();
            SetOperationRunning(true);
            try
            {
                await Task.Run(() => DiskManager.ConvertToGpt(disk.DiskNumber, AppendLog));
                RefreshDiskList(srcNum);
            }
            finally
            {
                SetOperationRunning(false);
            }
        }

        private async void BtnWipe_Click(object? sender, EventArgs e)
        {
            var targets = GetSelectedTargetDisks();
            if (targets.Count == 0)
            {
                MessageBox.Show("Seleziona almeno un disco nella colonna 'Seleziona' per cancellare le partizioni.",
                    "Nessun disco selezionato", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string targetList = string.Join(", ", targets.Select(t => $"Disco {t.DiskNumber} ({t.Model})"));
            var confirm = MessageBox.Show(
                $"ATTENZIONE: Stai per eliminare la tabella partizioni (MBR/GPT) e smontare i volumi dei seguenti dischi:\n\n{targetList}\n\nTutti i dati presenti verranno azzerati in modo irreversibile.\n\nConfermi l'eliminazione?",
                "Conferma Cancellazione Partizioni", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation);

            if (confirm != DialogResult.Yes) return;

            int sourceDiskNum = GetCurrentSourceDiskNumber();
            var targetDiskNums = targets.Select(t => t.DiskNumber).ToHashSet();

            SetOperationRunning(true);
            try
            {
                await Task.Run(() =>
                {
                    foreach (var disk in targets)
                    {
                        DiskManager.WipeDisk(disk.DiskNumber, AppendLog);
                    }
                });

                // Attendi 1000 ms per dare tempo a WinPE di smontare i vecchi volumi
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                AppendLog($"[ERRORE] Durante il wipe: {ex.Message}");
            }
            finally
            {
                SetOperationRunning(false);
            }

            // Richiama esplicitamente sul thread UI (BeginInvoke) l'aggiornamento preservando sorgente e spunte
            this.BeginInvoke(new Action(() =>
            {
                RefreshDiskList(sourceDiskNum, targetDiskNums, force: true);
                AppendLog("Cancellazione partizioni completata. Dischi puliti e pronti per la clonazione.");
                MessageBox.Show("Cancellazione partizioni completata con successo.\n\nI dischi selezionati sono stati puliti e sono pronti per essere clonati.", 
                    "Operazione Eseguita", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }));
        }

        private async void BtnClone_Click(object? sender, EventArgs e)
        {
            if (cmbSource.SelectedIndex < 0)
            {
                MessageBox.Show("Seleziona un disco sorgente valido.", "Sorgente Mancante", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var sourceDisk = currentDisks[cmbSource.SelectedIndex];
            var targets = GetSelectedTargetDisks();

            if (targets.Count == 0)
            {
                MessageBox.Show("Seleziona almeno un disco di destinazione tramite le caselle di controllo.",
                    "Destinazioni Mancanti", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (targets.Any(t => t.DiskNumber == sourceDisk.DiskNumber))
            {
                MessageBox.Show("ERRORE DI SICUREZZA: Il disco sorgente è selezionato come destinazione!", "Operazione Rifiutata",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // CONTROLLO IBRIDO: Caso Sorgente > Destinazione
            var smallerTargets = targets.Where(t => t.TotalSizeBytes < sourceDisk.TotalSizeBytes).ToList();
            if (smallerTargets.Count > 0)
            {
                string smallDetails = string.Join("\n", smallerTargets.Select(t =>
                    $"• Disco {t.DiskNumber} ({t.Model}): {t.CleanCapacity} (Sorgente: {sourceDisk.CleanCapacity})"));

                long usedOnSource = sourceDisk.UsedSizeBytes > 0 ? sourceDisk.UsedSizeBytes : sourceDisk.TotalSizeBytes;
                string usedFormatted = DiskInfo.FormatCleanSize(usedOnSource);

                bool allFitUsedSpace = smallerTargets.All(t => t.TotalSizeBytes >= usedOnSource);

                if (allFitUsedSpace)
                {
                    var hybridChoice = MessageBox.Show(
                        $"AVVISO DIMENSIONI DISCHI (Sorgente > Destinazione):\n\nI seguenti dischi di destinazione hanno una capienza totale inferiore alla sorgente:\n{smallDetails}\n\nTuttavia, lo spazio effettivamente occupato dai dati sulla sorgente è di {usedFormatted} e può rientrare nelle destinazioni.\n\nLa copia fisica RAW settore-per-settore è bloccata per evitare la corruzione della tabella partizioni. Desideri procedere con la clonazione a livello di partizione/filesystem (DISM/Volume Clone) per questi dischi?",
                        "Capienza Inferiore — Proposta Clonazione Filesystem",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information);

                    if (hybridChoice != DialogResult.Yes)
                    {
                        AppendLog("Clonazione interrotta dall'utente a causa della differenza di capienza.");
                        return;
                    }

                    AppendLog("Avvio procedura clonazione filesystem per i dischi a capienza ridotta...");
                }
                else
                {
                    MessageBox.Show(
                        $"IMPOSSIBILE CLONARE:\n\nI seguenti dischi sono troppo piccoli anche per i soli dati effettivi della sorgente:\n{smallDetails}\n\nSpazio dati effettivo sorgente: {usedFormatted}.\nLibera spazio o utilizza dischi di capacità adeguata.",
                        "Capienza Insufficiente", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            // Conferma Standard
            string targetSummary = string.Join(", ", targets.Select(t => $"Disco {t.DiskNumber} ({t.CleanCapacity})"));
            var confirm = MessageBox.Show(
                $"CONFERMA CLONAZIONE 1-a-N:\n\n• SORGENTE: Disco {sourceDisk.DiskNumber} ({sourceDisk.Model} — {sourceDisk.CleanCapacity})\n• DESTINAZIONI ({targets.Count}): {targetSummary}\n\nTutti i dischi di destinazione selezionati verranno SOVRASCRITTI completamente.\n\nProcedere?",
                "TessyClone — Conferma Avvio", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

            SetOperationRunning(true);
            cts = new CancellationTokenSource();

            var progressReporter = new Progress<CloneProgressReport>(report =>
            {
                if (this.IsDisposed || !this.IsHandleCreated) return;
                this.BeginInvoke(new Action(() =>
                {
                    int val = (int)(report.Percentage * 10);
                    progressBar.Value = Math.Clamp(val, 0, 1000);

                    if (!string.IsNullOrEmpty(report.PhaseMessage))
                    {
                        lblProgressStatus.Text = $"{report.PhaseMessage} ({report.Percentage:F0}%)";
                    }
                    else
                    {
                        double copiedGB = report.BytesCopied / (1024.0 * 1024.0 * 1024.0);
                        double totalGB = report.TotalBytes / (1024.0 * 1024.0 * 1024.0);

                        lblProgressStatus.Text = $"Progresso: {report.Percentage:F1}% | Velocità: {report.SpeedMBs:F1} MB/s | " +
                                                $"{copiedGB:F1} GB / {totalGB:F1} GB | Rimanente: {report.EstimatedRemaining:mm\\:ss}";
                    }

                    foreach (DataGridViewRow row in gridDisks.Rows)
                    {
                        if (row.Tag is DiskInfo d && report.TargetStatuses.TryGetValue(d.DiskNumber, out var status))
                        {
                            row.Cells["ColStatus"].Value = status;
                        }
                    }
                }));
            });

            bool success = false;
            try
            {
                bool parallelMode = chkParallelMode.Checked;
                success = await CloneEngine.CloneOneToManyAsync(sourceDisk, targets, progressReporter, AppendLog, cts.Token, parallelMode);
            }
            catch (Exception ex)
            {
                AppendLog($"[ERRORE CRITICO] {ex.Message}");
            }
            finally
            {
                SetOperationRunning(false);
            }

            if (success)
            {
                progressBar.Value = 1000;
                lblProgressStatus.Text = "Clonazione e finalizzazione completate con successo al 100%!";
                AppendLog("Tutte le clonazioni sono state completate con successo.");
                RefreshDiskList();

                if (chkAutoShutdown.Checked)
                {
                    using var countdownDlg = new CountdownDialog();
                    countdownDlg.ShowDialog(this);

                    if (countdownDlg.WasCancelled)
                    {
                        AppendLog("Spegnimento automatico annullato dall'utente. Il registro è disponibile a video.");
                    }
                    else
                    {
                        AppendLog("Avvio spegnimento automatico del sistema...");
                        ShutdownHelper.ShutdownComputer(AppendLog);
                    }
                }
                else
                {
                    MessageBox.Show("Tutte le clonazioni sono terminate con successo!", "TessyClone",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            else
            {
                MessageBox.Show("La clonazione è terminata con segnalazioni o errori. Controlla il registro operativo.",
                    "Avviso Operazione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 3. Chiusura app con X -> Popup "Vuoi spegnere il PC?"
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            if (isOperationRunning)
            {
                var warn = MessageBox.Show(
                    "È in corso un'operazione di clonazione o pulizia. Vuoi davvero interrompere ed uscire?",
                    "Operazione in corso", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (warn != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            var confirm = MessageBox.Show(
                "Vuoi spegnere il PC?",
                "TessyClone — Spegnimento",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm == DialogResult.Yes)
            {
                ShutdownHelper.ShutdownComputer(AppendLog);
            }
            else
            {
                e.Cancel = true;
            }
        }

        private void SetOperationRunning(bool running)
        {
            isOperationRunning = running;
            btnClone.Enabled = !running;
            btnWipe.Enabled = !running;
            btnRefresh.Enabled = !running;
            cmbSource.Enabled = !running;
            btnCancel.Enabled = running;
            gridDisks.Enabled = !running;
            chkParallelMode.Enabled = !running;
        }

        private void GridDisks_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex >= 0 && gridDisks.Columns[e.ColumnIndex].Name == "ColStatus" && e.CellStyle != null)
            {
                string? val = e.Value?.ToString();
                if (!string.IsNullOrEmpty(val))
                {
                    if (val.StartsWith("FALLITO", StringComparison.OrdinalIgnoreCase))
                    {
                        e.CellStyle.ForeColor = Color.FromArgb(231, 76, 60); // Rosso acceso
                        e.CellStyle.Font = new Font(gridDisks.Font, FontStyle.Bold);
                    }
                    else if (val.StartsWith("Completato", StringComparison.OrdinalIgnoreCase))
                    {
                        e.CellStyle.ForeColor = Color.FromArgb(39, 174, 96); // Verde brillante
                        e.CellStyle.Font = new Font(gridDisks.Font, FontStyle.Bold);
                    }
                    else if (val.StartsWith("Clonazione", StringComparison.OrdinalIgnoreCase))
                    {
                        e.CellStyle.ForeColor = Color.FromArgb(41, 128, 185); // Blu intenso
                        e.CellStyle.Font = new Font(gridDisks.Font, FontStyle.Bold);
                    }
                    else if (val.StartsWith("In coda", StringComparison.OrdinalIgnoreCase))
                    {
                        e.CellStyle.ForeColor = Color.FromArgb(127, 140, 141); // Grigio neutro
                    }
                }
            }
        }
    }
}
