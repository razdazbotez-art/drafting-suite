using System;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DraftingSuite
{
    internal static class DraftingSuitePalette
    {
        private const string PaletteSetName = "DRAFTINGSUITE";
        private const string PaletteSetCommand = "DS";
        private static readonly Guid PaletteSetGuid = new Guid("79e5d67a-5988-4b5d-97e7-4339d1df7d94");
        private static readonly Size DefaultPaletteSize = new Size(340, 300);
        private static PaletteSet paletteSet;

        internal static string StatusPaletteSetName => PaletteSetName;
        internal static string StatusPaletteSetGuid => PaletteSetGuid.ToString("D");

        public static void ShowPalette()
        {
            EnsureCreated();
            paletteSet.Visible = true;
            paletteSet.Activate(0);
        }

        private static void EnsureCreated()
        {
            if (paletteSet != null)
                return;

            paletteSet = new PaletteSet(PaletteSetName, PaletteSetCommand, PaletteSetGuid)
            {
                Style = PaletteSetStyles.ShowAutoHideButton |
                        PaletteSetStyles.ShowCloseButton |
                        PaletteSetStyles.ShowPropertiesMenu |
                        PaletteSetStyles.Snappable,
                MinimumSize = new Size(300, 240),
                Size = DefaultPaletteSize,
                DockEnabled = DockSides.Left | DockSides.Right
            };
            paletteSet.Add("Drafting", new DraftingSuitePaletteControl());
        }

        private sealed class DraftingSuitePaletteControl : UserControl
        {
            private static readonly Color ButtonBackColor = Color.FromArgb(248, 249, 251);
            private static readonly Color ButtonBorderColor = Color.FromArgb(214, 219, 226);
            private static readonly Color TextColor = Color.FromArgb(32, 37, 45);
            private static readonly Color MutedTextColor = Color.FromArgb(75, 85, 99);

            public DraftingSuitePaletteControl()
            {
                Font = new Font("Segoe UI", 8.5f);
                BackColor = Color.White;
                AutoScroll = true;
                Dock = DockStyle.Fill;

                TableLayoutPanel root = new TableLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    ColumnCount = 1,
                    Padding = new Padding(10)
                };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                Controls.Add(root);

                TableLayoutPanel prep = AddSection(root, "FBK Prep");
                AddButton(prep, "Prepare FBK", "_.DSFBKPREP ");
                AddButton(prep, "Settings", null, (_, __) => DraftingSuiteSettingsForm.ShowSettingsDialog());

                TableLayoutPanel help = AddSection(root, "Status");
                AddValueRow(help, "Version", Commands.VersionText);
                AddValueRow(help, "Preset", DraftingSuiteSettings.LoadActiveSettings().PresetName);
                AddButton(help, "Command List", "_.DSVERSION ");
            }

            private TableLayoutPanel AddSection(TableLayoutPanel root, string title)
            {
                Panel wrapper = new Panel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Margin = new Padding(0, 0, 0, 6)
                };

                TableLayoutPanel shell = new TableLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    ColumnCount = 1,
                    Margin = new Padding(0)
                };
                shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

                Label header = new Label
                {
                    Text = title,
                    Dock = DockStyle.Top,
                    Height = 24,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font(Font, FontStyle.Bold),
                    ForeColor = TextColor,
                    BackColor = Color.FromArgb(232, 237, 242),
                    Padding = new Padding(6, 0, 0, 0),
                    Margin = new Padding(0)
                };

                TableLayoutPanel body = new TableLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    ColumnCount = 1,
                    Padding = new Padding(0, 4, 0, 0)
                };
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

                shell.Controls.Add(header);
                shell.Controls.Add(body);
                wrapper.Controls.Add(shell);
                root.Controls.Add(wrapper);
                return body;
            }

            private static void AddButton(TableLayoutPanel section, string text, string command, EventHandler click = null)
            {
                Button button = new Button
                {
                    Text = text,
                    Dock = DockStyle.Top,
                    Height = 28,
                    Margin = new Padding(0, 1, 0, 3),
                    BackColor = ButtonBackColor,
                    ForeColor = TextColor,
                    FlatStyle = FlatStyle.Flat
                };
                button.FlatAppearance.BorderColor = ButtonBorderColor;
                button.Click += click ?? ((_, __) => RunCommand(command));
                section.Controls.Add(button);
            }

            private static void AddValueRow(TableLayoutPanel section, string labelText, string value)
            {
                TableLayoutPanel row = new TableLayoutPanel
                {
                    Dock = DockStyle.Top,
                    Height = 24,
                    ColumnCount = 2,
                    Margin = new Padding(0, 1, 0, 1)
                };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70f));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

                row.Controls.Add(new Label
                {
                    Text = labelText,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = MutedTextColor
                }, 0, 0);
                row.Controls.Add(new Label
                {
                    Text = string.IsNullOrWhiteSpace(value) ? "Typical" : value,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = TextColor
                }, 1, 0);
                section.Controls.Add(row);
            }

            private static void RunCommand(string command)
            {
                if (string.IsNullOrWhiteSpace(command))
                    return;

                Document doc = AcadApplication.DocumentManager.MdiActiveDocument;
                if (doc == null)
                    return;

                doc.SendStringToExecute("\u001b\u001b" + command, true, false, false);
            }
        }
    }
}
