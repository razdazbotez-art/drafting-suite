using System;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DraftingSuite
{
    internal sealed class DraftingSuitePalette : Form
    {
        private static DraftingSuitePalette instance;

        private DraftingSuitePalette()
        {
            Text = "Drafting Suite";
            Width = 320;
            Height = 240;
            MinimumSize = new Size(280, 210);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(12)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label title = new Label
            {
                Text = "FBK Prep",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 8)
            };

            Button runButton = new Button
            {
                Text = "Prepare FBK",
                Height = 32,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 10)
            };
            runButton.Click += (_, __) => RunCommand("DSFBKPREP");

            Button settingsButton = new Button
            {
                Text = "Settings",
                Height = 30,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 10)
            };
            settingsButton.Click += (_, __) => DraftingSuiteSettingsForm.ShowSettingsDialog();

            Label details = new Label
            {
                Text = "Extract COGO display graphics, convert text to mleaders, flatten annotation, and set COGO styles.",
                Dock = DockStyle.Fill,
                AutoSize = false
            };

            Label command = new Label
            {
                Text = "Command: DSFBKPREP",
                AutoSize = true,
                ForeColor = SystemColors.GrayText
            };

            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(runButton, 0, 1);
            layout.Controls.Add(settingsButton, 0, 2);
            layout.Controls.Add(details, 0, 3);
            layout.Controls.Add(command, 0, 4);
            Controls.Add(layout);
        }

        public static void ShowPalette()
        {
            if (instance == null || instance.IsDisposed)
            {
                instance = new DraftingSuitePalette();
                AcadApplication.ShowModelessDialog(instance);
                return;
            }

            if (instance.WindowState == FormWindowState.Minimized)
                instance.WindowState = FormWindowState.Normal;

            instance.Show();
            instance.Activate();
        }

        private void RunCommand(string command)
        {
            Document doc = AcadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            WindowState = FormWindowState.Minimized;
            doc.SendStringToExecute("\u001b\u001b" + command + " ", true, false, false);
        }
    }
}
