using System;
using System.Collections.Generic;
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
            paletteSet.Add("Pad", new DraftingSuitePaletteControl(DraftingSuitePaletteTab.Pad));
            paletteSet.Add("Help", new DraftingSuitePaletteControl(DraftingSuitePaletteTab.Help));
        }

        private enum DraftingSuitePaletteTab
        {
            Pad,
            Help
        }

        private sealed class DraftingSuitePaletteControl : UserControl
        {
            private static readonly Color ButtonBackColor = Color.FromArgb(248, 249, 251);
            private static readonly Color ButtonBorderColor = Color.FromArgb(214, 219, 226);
            private static readonly Color TextColor = Color.FromArgb(32, 37, 45);
            private static readonly Color MutedTextColor = Color.FromArgb(75, 85, 99);
            private readonly TableLayoutPanel currentRoot;
            private readonly ToolTip toolTip = new ToolTip();
            private readonly List<Button> padButtons = new List<Button>();
            private TableLayoutPanel padGrid;

            public DraftingSuitePaletteControl(DraftingSuitePaletteTab tab)
            {
                Font = new Font("Segoe UI", 8.5f);
                BackColor = Color.White;
                AutoScroll = true;
                Dock = DockStyle.Fill;

                currentRoot = new TableLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    ColumnCount = 1,
                    Padding = new Padding(10)
                };
                currentRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                Controls.Add(currentRoot);

                if (tab == DraftingSuitePaletteTab.Pad)
                    BuildPadTab();
                else
                    BuildHelpTab();

                Resize += (_, __) => ConfigurePadGridColumns();
            }

            private void BuildPadTab()
            {
                TableLayoutPanel pad = AddSection("Command Pad");
                AddPadButtonGrid(pad);
            }

            private void BuildHelpTab()
            {
                TableLayoutPanel help = AddSection("Help");
                AddValueRow(help, "Version", Commands.VersionText);
                AddValueRow(help, "Preset", DraftingSuiteSettings.LoadActiveSettings().PresetName);
                AddButton(help, "Command List", "_.DSVERSION ");
            }

            private void AddPadButtonGrid(TableLayoutPanel section)
            {
                padGrid = new TableLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Margin = new Padding(0)
                };
                section.Controls.Add(padGrid);

                AddPadButton("FBK", "_.DSFBKPREP ", "Prepare the opened FBK drawing using the active Drafting Suite preset.");
                AddPadButton("Settings", "_.DSSETTINGS ", "Open Drafting Suite settings and presets.");
                AddPadButton("MT2ML", "_.DSMT2ML ", "Convert selected text or mtext to mleaders using the configured leader offset.");
                AddPadButton("Tiny", "_.DSDELETETINY ", "Delete selected text or mtext below the configured tiny text height.");
                AddPadButton("Flat", "_.DSFLATTEN ", "Flatten selected drafting annotation to the configured elevation.");
                AddPadButton("ByLayer", "_.DSBYLAYER ", "Set selected objects to ByLayer color, linetype, and lineweight.");
                AddPadButton("3DPoly", "_.DSLINE3D ", "Convert selected lines to 3D polylines and delete COGO-layer lines.");
                AddPadButton("COGO", "_.DSCOGOSTD ", "Set selected COGO points to the configured point and label styles.");
                ConfigurePadGridColumns();
            }

            private void AddPadButton(string text, string command, string description)
            {
                Button button = CreateButton(text, command);
                button.Height = 34;
                button.Margin = new Padding(0, 1, 4, 4);
                padButtons.Add(button);
                padGrid.Controls.Add(button);

                toolTip.SetToolTip(button, description + Environment.NewLine + "Command: " + command.Trim().TrimStart('_', '.'));
            }

            private void ConfigurePadGridColumns()
            {
                if (padGrid == null)
                    return;

                int columns = ClientSize.Width >= 430 ? 3 : 2;
                if (padGrid.ColumnCount == columns && padGrid.Controls.Count == padButtons.Count)
                    return;

                padGrid.SuspendLayout();
                padGrid.Controls.Clear();
                padGrid.ColumnStyles.Clear();
                padGrid.RowStyles.Clear();
                padGrid.ColumnCount = columns;
                padGrid.RowCount = (int)Math.Ceiling(padButtons.Count / (double)columns);
                for (int i = 0; i < columns; i++)
                    padGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
                for (int i = 0; i < padButtons.Count; i++)
                    padGrid.Controls.Add(padButtons[i], i % columns, i / columns);
                padGrid.ResumeLayout();
            }

            private TableLayoutPanel AddSection(string title)
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
                currentRoot.Controls.Add(wrapper);
                return body;
            }

            private static void AddButton(TableLayoutPanel section, string text, string command, EventHandler click = null)
            {
                Button button = CreateButton(text, command);
                button.Height = 28;
                button.Margin = new Padding(0, 1, 0, 3);
                if (click != null)
                {
                    button.Click -= RunButtonCommand;
                    button.Click += click;
                }
                section.Controls.Add(button);
            }

            private static Button CreateButton(string text, string command)
            {
                Button button = new Button
                {
                    Text = text,
                    Dock = DockStyle.Top,
                    BackColor = ButtonBackColor,
                    ForeColor = TextColor,
                    FlatStyle = FlatStyle.Flat,
                    Tag = command
                };
                button.FlatAppearance.BorderColor = ButtonBorderColor;
                button.Click += RunButtonCommand;
                return button;
            }

            private static void RunButtonCommand(object sender, EventArgs e)
            {
                Button button = sender as Button;
                RunCommand(button?.Tag as string);
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
