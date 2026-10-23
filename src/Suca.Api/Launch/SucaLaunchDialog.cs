using System.Drawing;
using System.Windows.Forms;

namespace SUCA_Compressor
{
    public sealed class SucaLaunchDialog : Form
    {
        private SucaLaunchMode _selectedMode;

        private SucaLaunchDialog()
        {
            Text = "SUCA Launcher";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(560, 330);
            BackColor = Color.FromArgb(244, 247, 250);
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 88,
                BackColor = Color.FromArgb(20, 38, 61)
            };

            header.Controls.Add(new Label
            {
                AutoSize = true,
                Location = new Point(28, 16),
                Text = "SUCA",
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 22F, FontStyle.Bold, GraphicsUnit.Point)
            });
            header.Controls.Add(new Label
            {
                AutoSize = true,
                Location = new Point(31, 53),
                Text = "Compresion de imagenes y servicios de inteligencia",
                ForeColor = Color.FromArgb(205, 218, 232),
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point)
            });
            Controls.Add(header);

            Controls.Add(new Label
            {
                AutoSize = true,
                Location = new Point(30, 111),
                Text = "Como desea iniciar SUCA?",
                ForeColor = Color.FromArgb(25, 42, 62),
                Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold, GraphicsUnit.Point)
            });

            Controls.Add(CreateModeButton(
                "Iniciar servicio web",
                "Expone REST, MCP y el minichat en el navegador.",
                new Point(30, 151),
                Color.FromArgb(15, 110, 176),
                SucaLaunchMode.Web));

            Controls.Add(CreateModeButton(
                "Abrir consola",
                "Solicita una imagen y ejecuta el flujo local de compresion.",
                new Point(292, 151),
                Color.FromArgb(38, 123, 91),
                SucaLaunchMode.Console));

            var cancel = new Button
            {
                Text = "Cancelar",
                Size = new Size(104, 34),
                Location = new Point(426, 278),
                DialogResult = DialogResult.Cancel,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent
            };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(170, 180, 190);
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        public static SucaLaunchMode? ShowModeDialog()
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            using var dialog = new SucaLaunchDialog();
            return dialog.ShowDialog() == DialogResult.OK ? dialog._selectedMode : null;
        }

        private Button CreateModeButton(string title, string description, Point location, Color color, SucaLaunchMode mode)
        {
            var button = new Button
            {
                Location = location,
                Size = new Size(238, 104),
                Text = title + Environment.NewLine + description,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 8, 12, 8),
                ForeColor = Color.White,
                BackColor = color,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point),
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => SelectMode(mode);
            return button;
        }

        private void SelectMode(SucaLaunchMode mode)
        {
            _selectedMode = mode;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
