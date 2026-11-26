using System;
using System.Drawing;
using System.Windows.Forms;

namespace WinPECloner
{
    public class CountdownDialog : Form
    {
        private int remainingSeconds = 5;
        private System.Windows.Forms.Timer countdownTimer = null!;
        private Label lblMessage = null!;
        private Button btnCancel = null!;
        private Button btnShutdownNow = null!;
        private ProgressBar pgbCountdown = null!;

        public bool WasCancelled { get; private set; } = false;

        public CountdownDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Spegnimento Automatico Programmato";
            this.Size = new Size(520, 260);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(245, 247, 250);
            this.Font = new Font("Segoe UI", 10f);

            var pnlTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                BackColor = Color.FromArgb(44, 62, 80),
                Padding = new Padding(15)
            };

            var lblTitle = new Label
            {
                Text = "⏱️ Tutte le clonazioni sono terminate con successo!",
                Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
                ForeColor = Color.White,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            pnlTop.Controls.Add(lblTitle);

            lblMessage = new Label
            {
                Text = $"Il computer si spegnerà automaticamente tra {remainingSeconds} secondi.\nPuoi annullare per controllare i dettagli del registro operativo.",
                Location = new Point(25, 75),
                Size = new Size(460, 48),
                Font = new Font("Segoe UI", 10f, FontStyle.Regular),
                ForeColor = Color.FromArgb(44, 62, 80)
            };

            pgbCountdown = new ProgressBar
            {
                Location = new Point(25, 130),
                Size = new Size(455, 22),
                Minimum = 0,
                Maximum = 5,
                Value = 5
            };

            btnCancel = new Button
            {
                Text = "Annulla Spegnimento",
                Location = new Point(25, 168),
                Size = new Size(200, 38),
                BackColor = Color.FromArgb(231, 76, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Click += (s, e) =>
            {
                WasCancelled = true;
                countdownTimer.Stop();
                this.Close();
            };

            btnShutdownNow = new Button
            {
                Text = "Spegni Ora",
                Location = new Point(280, 168),
                Size = new Size(200, 38),
                BackColor = Color.FromArgb(52, 73, 94),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnShutdownNow.FlatAppearance.BorderSize = 0;
            btnShutdownNow.Click += (s, e) =>
            {
                WasCancelled = false;
                countdownTimer.Stop();
                this.Close();
            };

            this.Controls.Add(pnlTop);
            this.Controls.Add(lblMessage);
            this.Controls.Add(pgbCountdown);
            this.Controls.Add(btnCancel);
            this.Controls.Add(btnShutdownNow);

            countdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            countdownTimer.Tick += CountdownTimer_Tick;
            countdownTimer.Start();
        }

        private void CountdownTimer_Tick(object? sender, EventArgs e)
        {
            remainingSeconds--;
            if (remainingSeconds <= 0)
            {
                countdownTimer.Stop();
                WasCancelled = false;
                this.Close();
                return;
            }

            pgbCountdown.Value = Math.Max(0, remainingSeconds);
            lblMessage.Text = $"Il computer si spegnerà automaticamente tra {remainingSeconds} secondi.\nPuoi annullare per controllare i dettagli del registro operativo.";
        }
    }
}
