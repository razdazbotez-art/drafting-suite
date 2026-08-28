using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DraftingSuite
{
    internal sealed class DraftingSuiteSettingsForm : Form
    {
        private ComboBox presetCombo;
        private TextBox presetFolderBox;
        private Label presetStatus;
        private CheckBox extractCogoCheck;
        private CheckBox convertTextCheck;
        private CheckBox flattenCheck;
        private CheckBox restyleCheck;
        private CheckBox convertLinesCheck;
        private CheckBox deleteSurveyNetworksCheck;
        private TextBox offsetXBox;
        private TextBox offsetYBox;
        private TextBox flattenElevationBox;
        private TextBox pointStyleBox;
        private TextBox labelStyleBox;
        private TextBox protectedLayersBox;
        private TextBox resultLayersBox;
        private TextBox annotationLayersBox;
        private TextBox mleaderDeleteLayersBox;
        private TextBox mleaderKeepTextLayersBox;
        private TextBox flattenSkipBlocksBox;
        private TextBox tinyTextBox;
        private NumericUpDown explodeBeforeBox;
        private CheckBox burstCheck;
        private NumericUpDown explodeAfterBox;
        private NumericUpDown maxAnonymousBurstPassesBox;
        private string defaultPresetName;

        private DraftingSuiteSettingsForm()
        {
            Text = "Drafting Suite Settings";
            Width = 700;
            Height = 560;
            MinimumSize = new System.Drawing.Size(620, 500);
            StartPosition = FormStartPosition.CenterScreen;

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(BuildPresetPanel(), 0, 0);
            root.Controls.Add(BuildFieldsPanel(), 0, 1);
            root.Controls.Add(BuildDialogButtons(), 0, 2);
            Controls.Add(root);

            DraftingSuiteSettings active = DraftingSuiteSettings.LoadActiveSettings();
            defaultPresetName = active.DefaultPresetName;
            LoadSettings(active);
            RefreshPresetList(string.IsNullOrWhiteSpace(active.PresetName) ? active.DefaultPresetName : active.PresetName);
        }

        public static void ShowSettingsDialog()
        {
            using (DraftingSuiteSettingsForm form = new DraftingSuiteSettingsForm())
            {
                AcadApplication.ShowModalDialog(form);
            }
        }

        private Control BuildPresetPanel()
        {
            GroupBox group = new GroupBox
            {
                Text = "Presets",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(10, 8, 10, 10)
            };

            TableLayoutPanel panel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                Padding = new Padding(0)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            presetCombo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            Button browseFolder = new Button { Text = "Folder", Width = 76 };
            browseFolder.Click += (_, __) => BrowsePresetFolder();

            presetFolderBox = new TextBox { Dock = DockStyle.Top };
            presetFolderBox.Leave += (_, __) => RefreshPresetList(presetCombo.Text);

            presetStatus = new Label { AutoSize = true, Dock = DockStyle.Top, ForeColor = System.Drawing.SystemColors.GrayText };

            FlowLayoutPanel presetButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };
            Button loadPreset = new Button { Text = "Load", Width = 72 };
            Button saveActive = new Button { Text = "Save", Width = 72 };
            Button savePreset = new Button { Text = "Save As", Width = 78 };
            Button renamePreset = new Button { Text = "Rename", Width = 78 };
            Button deletePreset = new Button { Text = "Delete", Width = 72 };
            Button setDefault = new Button { Text = "Set Default", Width = 92 };
            loadPreset.Click += (_, __) => LoadSelectedPreset();
            saveActive.Click += (_, __) => SaveCurrentSettings(true);
            savePreset.Click += (_, __) => SaveAsPreset();
            renamePreset.Click += (_, __) => RenameSelectedPreset();
            deletePreset.Click += (_, __) => DeleteSelectedPreset();
            setDefault.Click += (_, __) => SetDefaultPreset();
            presetButtons.Controls.Add(loadPreset);
            presetButtons.Controls.Add(saveActive);
            presetButtons.Controls.Add(savePreset);
            presetButtons.Controls.Add(renamePreset);
            presetButtons.Controls.Add(deletePreset);
            presetButtons.Controls.Add(setDefault);

            panel.Controls.Add(new Label { Text = "Preset", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            panel.Controls.Add(presetCombo, 1, 0);
            panel.Controls.Add(new Label(), 2, 0);
            panel.Controls.Add(new Label { Text = "Preset folder", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            panel.Controls.Add(presetFolderBox, 1, 1);
            panel.Controls.Add(browseFolder, 2, 1);
            panel.Controls.Add(new Label(), 0, 2);
            panel.Controls.Add(presetStatus, 1, 2);
            panel.Controls.Add(new Label(), 2, 2);
            panel.Controls.Add(new Label(), 0, 3);
            panel.Controls.Add(presetButtons, 1, 3);
            panel.Controls.Add(new Label(), 2, 3);
            group.Controls.Add(panel);
            return group;
        }

        private Control BuildFieldsPanel()
        {
            TabControl tabs = new TabControl
            {
                Dock = DockStyle.Fill
            };

            TableLayoutPanel workflow = CreateFieldsTable();
            extractCogoCheck = AddCheck(workflow, "Extract COGO graphics");
            deleteSurveyNetworksCheck = AddCheck(workflow, "Delete survey network");
            convertLinesCheck = AddCheck(workflow, "Convert lines to 3D polylines");
            restyleCheck = AddCheck(workflow, "Restyle COGO points");
            explodeBeforeBox = AddNumber(workflow, "Explode passes before burst");
            AddHelpText(workflow, "Explodes regular block references created from COGO graphics before anonymous blocks are burst.");
            burstCheck = AddCheck(workflow, "Burst anonymous blocks");
            maxAnonymousBurstPassesBox = AddNumber(workflow, "Max anonymous burst passes");
            explodeAfterBox = AddNumber(workflow, "Explode passes after burst");
            AddHelpText(workflow, "Runs more regular block-reference explode passes after bursting, so nested blocks exposed by the burst can still become editable objects.");
            tabs.TabPages.Add(CreateTabPage("Workflow", workflow));

            TableLayoutPanel text = CreateFieldsTable();
            convertTextCheck = AddCheck(text, "Convert text to mleaders");
            tinyTextBox = AddText(text, "Tiny text max height");
            AddHelpText(text, "Deletes DBText or MText whose text height is this value or smaller before mleader conversion.");
            mleaderDeleteLayersBox = AddMultiline(text, "Delete text layers");
            mleaderKeepTextLayersBox = AddMultiline(text, "Keep as text layers");
            offsetXBox = AddText(text, "MLeader offset X");
            offsetYBox = AddText(text, "MLeader offset Y");
            tabs.TabPages.Add(CreateTabPage("Text && MLeaders", text));

            TableLayoutPanel layers = CreateFieldsTable();
            flattenCheck = AddCheck(layers, "Flatten annotation");
            AddHelpText(layers, "Sets supported annotation objects to the flatten elevation during FBK Prep.");
            flattenElevationBox = AddText(layers, "Flatten elevation");
            AddHelpText(layers, "Elevation value used when flattening annotation, usually 0.");
            flattenSkipBlocksBox = AddMultiline(layers, "Do not flatten blocks");
            AddHelpText(layers, "Wildcard block-name list skipped by flattening, such as BL_PT_*.");
            protectedLayersBox = AddMultiline(layers, "COGO point layers");
            AddHelpText(layers, "Wildcard layer list saved with presets for COGO point source graphics. It documents the client standard while FBK Prep keeps generated blocks unless they successfully explode or burst.");
            resultLayersBox = AddMultiline(layers, "Groundshot point layers");
            AddHelpText(layers, "Wildcard layer list saved with presets for groundshot point graphics. It documents the client standard and does not remove generated block output.");
            annotationLayersBox = AddMultiline(layers, "Annotation layers to keep");
            AddHelpText(layers, "Wildcard layer list saved with presets for expected annotation output. Text deletion and keep-as-text behavior is controlled by the Text and MLeaders tab.");
            tabs.TabPages.Add(CreateTabPage("Layers && Flatten", layers));

            TableLayoutPanel cogo = CreateFieldsTable();
            pointStyleBox = AddText(cogo, "COGO point style");
            labelStyleBox = AddText(cogo, "COGO label style");
            tabs.TabPages.Add(CreateTabPage("COGO", cogo));

            return tabs;
        }

        private static TableLayoutPanel CreateFieldsTable()
        {
            TableLayoutPanel fields = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Padding = new Padding(10)
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return fields;
        }

        private static TabPage CreateTabPage(string title, Control content)
        {
            Panel scrollHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };
            scrollHost.Controls.Add(content);

            TabPage page = new TabPage(title)
            {
                Padding = new Padding(0)
            };
            page.Controls.Add(scrollHost);
            return page;
        }

        private Control BuildDialogButtons()
        {
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true
            };
            Button ok = new Button { Text = "OK", Width = 84 };
            Button cancel = new Button { Text = "Cancel", Width = 84 };
            Button reset = new Button { Text = "Reset", Width = 84 };
            ok.Click += (_, __) => SaveAndClose();
            cancel.Click += (_, __) => Close();
            reset.Click += (_, __) => LoadSettings(DraftingSuiteSettings.CreateDefault());
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(reset);
            return buttons;
        }

        private CheckBox AddCheck(TableLayoutPanel fields, string label)
        {
            Label rowLabel = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 3) };
            CheckBox check = new CheckBox { AutoSize = true, Anchor = AnchorStyles.Left };
            fields.Controls.Add(rowLabel);
            fields.Controls.Add(check);
            return check;
        }

        private TextBox AddText(TableLayoutPanel fields, string label)
        {
            Label rowLabel = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 3) };
            TextBox box = new TextBox { Dock = DockStyle.Top };
            fields.Controls.Add(rowLabel);
            fields.Controls.Add(box);
            return box;
        }

        private static void AddHelpText(TableLayoutPanel fields, string text)
        {
            Label spacer = new Label { AutoSize = true };
            Label help = new Label
            {
                Text = text,
                AutoSize = true,
                Dock = DockStyle.Top,
                ForeColor = System.Drawing.SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, 8),
                MaximumSize = new System.Drawing.Size(430, 0)
            };
            fields.Controls.Add(spacer);
            fields.Controls.Add(help);
        }

        private TextBox AddMultiline(TableLayoutPanel fields, string label)
        {
            Label rowLabel = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 3) };
            TextBox box = new TextBox
            {
                Dock = DockStyle.Top,
                Multiline = true,
                Height = 58,
                ScrollBars = ScrollBars.Vertical
            };
            fields.Controls.Add(rowLabel);
            fields.Controls.Add(box);
            return box;
        }

        private NumericUpDown AddNumber(TableLayoutPanel fields, string label)
        {
            Label rowLabel = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 3) };
            NumericUpDown box = new NumericUpDown
            {
                Dock = DockStyle.Top,
                Minimum = 0,
                Maximum = 10,
                DecimalPlaces = 0
            };
            fields.Controls.Add(rowLabel);
            fields.Controls.Add(box);
            return box;
        }

        private void LoadSettings(DraftingSuiteSettings settings)
        {
            extractCogoCheck.Checked = settings.ExtractCogoDisplayGraphics;
            convertTextCheck.Checked = settings.ConvertTextToMleaders;
            flattenCheck.Checked = settings.FlattenAnnotation;
            restyleCheck.Checked = settings.RestyleCogoPoints;
            convertLinesCheck.Checked = settings.ConvertLinesTo3dPolylines;
            deleteSurveyNetworksCheck.Checked = settings.DeleteSurveyNetworks;
            offsetXBox.Text = FormatDouble(settings.MLeaderTextOffsetX);
            offsetYBox.Text = FormatDouble(settings.MLeaderTextOffsetY);
            flattenElevationBox.Text = FormatDouble(settings.FlattenElevation);
            pointStyleBox.Text = settings.CogoPointStyleName;
            labelStyleBox.Text = settings.CogoLabelStyleName;
            protectedLayersBox.Text = JoinLines(settings.ProtectedSourceLayerPatterns);
            resultLayersBox.Text = JoinLines(settings.ResultLayerPatterns);
            annotationLayersBox.Text = JoinLines(settings.AnnotationLayerPatterns);
            mleaderDeleteLayersBox.Text = JoinLines(settings.MLeaderDeleteLayerPatterns);
            mleaderKeepTextLayersBox.Text = JoinLines(settings.MLeaderKeepTextLayerPatterns);
            flattenSkipBlocksBox.Text = JoinLines(settings.FlattenSkipBlockNamePatterns);
            tinyTextBox.Text = FormatDouble(settings.TinyTextDeleteHeight);
            explodeBeforeBox.Value = ClampDecimal(settings.ExplodePassesBeforeBurst, explodeBeforeBox.Minimum, explodeBeforeBox.Maximum);
            burstCheck.Checked = settings.BurstInserts;
            maxAnonymousBurstPassesBox.Value = ClampDecimal(settings.MaxAnonymousBurstPasses, maxAnonymousBurstPassesBox.Minimum, maxAnonymousBurstPassesBox.Maximum);
            explodeAfterBox.Value = ClampDecimal(settings.ExplodePassesAfterBurst, explodeAfterBox.Minimum, explodeAfterBox.Maximum);
            presetFolderBox.Text = settings.PresetFolderPath;
            defaultPresetName = settings.DefaultPresetName ?? defaultPresetName ?? string.Empty;
            SelectPreset(settings.PresetName);
            UpdatePresetStatus();
        }

        private DraftingSuiteSettings ReadSettingsFromForm()
        {
            if (!TryReadDouble(offsetXBox, "MLeader offset X", out double offsetX) ||
                !TryReadDouble(offsetYBox, "MLeader offset Y", out double offsetY) ||
                !TryReadDouble(flattenElevationBox, "Flatten elevation", out double flattenElevation) ||
                !TryReadDouble(tinyTextBox, "Tiny text max height", out double tinyTextHeight))
            {
                return null;
            }

            return new DraftingSuiteSettings
            {
                ExtractCogoDisplayGraphics = extractCogoCheck.Checked,
                ConvertTextToMleaders = convertTextCheck.Checked,
                FlattenAnnotation = flattenCheck.Checked,
                RestyleCogoPoints = restyleCheck.Checked,
                ConvertLinesTo3dPolylines = convertLinesCheck.Checked,
                DeleteSurveyNetworks = deleteSurveyNetworksCheck.Checked,
                MLeaderTextOffsetX = offsetX,
                MLeaderTextOffsetY = offsetY,
                FlattenElevation = flattenElevation,
                CogoPointStyleName = pointStyleBox.Text.Trim(),
                CogoLabelStyleName = labelStyleBox.Text.Trim(),
                ProtectedSourceLayerPatterns = SplitLines(protectedLayersBox.Text),
                ResultLayerPatterns = SplitLines(resultLayersBox.Text),
                AnnotationLayerPatterns = SplitLines(annotationLayersBox.Text),
                MLeaderIgnoreLayerPatterns = SplitLines(mleaderDeleteLayersBox.Text),
                MLeaderDeleteLayerPatterns = SplitLines(mleaderDeleteLayersBox.Text),
                MLeaderKeepTextLayerPatterns = SplitLines(mleaderKeepTextLayersBox.Text),
                FlattenSkipBlockNamePatterns = SplitLines(flattenSkipBlocksBox.Text),
                TinyTextDeleteHeight = tinyTextHeight,
                ExplodePassesBeforeBurst = (int)explodeBeforeBox.Value,
                BurstInserts = burstCheck.Checked,
                MaxAnonymousBurstPasses = (int)maxAnonymousBurstPassesBox.Value,
                ExplodePassesAfterBurst = (int)explodeAfterBox.Value,
                PresetName = presetCombo.Text,
                PresetFolderPath = presetFolderBox.Text.Trim(),
                DefaultPresetName = defaultPresetName
            };
        }

        private void SaveAndClose()
        {
            if (SaveCurrentSettings(true))
                Close();
        }

        private bool SaveCurrentSettings(bool showErrors)
        {
            DraftingSuiteSettings settings = ReadSettingsFromForm();
            if (settings == null)
                return false;

            try
            {
                settings.Save();
                if (!string.IsNullOrWhiteSpace(settings.PresetName))
                    settings.SavePreset(settings.PresetName);
                LoadSettings(DraftingSuiteSettings.LoadActiveSettings());
                UpdatePresetStatus();
                return true;
            }
            catch (Exception ex)
            {
                if (showErrors)
                    MessageBox.Show(this, ex.Message, "Settings could not be saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void LoadSelectedPreset()
        {
            if (string.IsNullOrWhiteSpace(presetCombo.Text))
                return;

            DraftingSuiteSettings preset = DraftingSuiteSettings.LoadPreset(presetCombo.Text, presetFolderBox.Text);
            if (preset == null)
            {
                MessageBox.Show(this, "Preset could not be loaded.", "Preset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            preset.DefaultPresetName = defaultPresetName;
            LoadSettings(preset);
        }

        private void SaveAsPreset()
        {
            DraftingSuiteSettings settings = ReadSettingsFromForm();
            if (settings == null)
                return;

            string presetName = PromptForText("Save Preset", "Preset name:", presetCombo.Text);
            if (string.IsNullOrWhiteSpace(presetName))
                return;

            try
            {
                settings.PresetName = presetName.Trim();
                settings.SavePreset(settings.PresetName);
                settings.Save();
                RefreshPresetList(settings.PresetName);
                UpdatePresetStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Preset could not be saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RenameSelectedPreset()
        {
            string oldName = presetCombo.Text;
            if (string.IsNullOrWhiteSpace(oldName))
                return;

            string newName = PromptForText("Rename Preset", "New preset name:", oldName);
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(oldName, newName.Trim(), StringComparison.OrdinalIgnoreCase))
                return;

            if (!DraftingSuiteSettings.RenamePreset(oldName, newName.Trim(), presetFolderBox.Text))
            {
                MessageBox.Show(this, "Preset could not be renamed. A preset with that name may already exist.", "Preset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.Equals(defaultPresetName, oldName, StringComparison.OrdinalIgnoreCase))
                defaultPresetName = newName.Trim();

            RefreshPresetList(newName.Trim());
            UpdatePresetStatus();
        }

        private void DeleteSelectedPreset()
        {
            string presetName = presetCombo.Text;
            if (string.IsNullOrWhiteSpace(presetName))
                return;

            DialogResult confirm = MessageBox.Show(this, "Delete preset '" + presetName + "'?", "Delete Preset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
                return;

            if (!DraftingSuiteSettings.DeletePreset(presetName, presetFolderBox.Text))
            {
                MessageBox.Show(this, "Preset could not be deleted.", "Preset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.Equals(defaultPresetName, presetName, StringComparison.OrdinalIgnoreCase))
                defaultPresetName = string.Empty;

            RefreshPresetList(string.Empty);
            UpdatePresetStatus();
        }

        private void SetDefaultPreset()
        {
            defaultPresetName = presetCombo.Text ?? string.Empty;
            DraftingSuiteSettings settings = ReadSettingsFromForm();
            if (settings == null)
                return;

            try
            {
                settings.DefaultPresetName = defaultPresetName;
                settings.Save();
                if (!string.IsNullOrWhiteSpace(defaultPresetName))
                    settings.SavePreset(defaultPresetName);
                UpdatePresetStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Default preset could not be saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BrowsePresetFolder()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select Drafting Suite preset folder";
                dialog.SelectedPath = Directory.Exists(presetFolderBox.Text) ? presetFolderBox.Text : DraftingSuiteSettings.DefaultPresetFolderPath;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                presetFolderBox.Text = dialog.SelectedPath;
                RefreshPresetList(string.Empty);
                UpdatePresetStatus();
            }
        }

        private void RefreshPresetList(string selectedName)
        {
            string[] names = DraftingSuiteSettings.ListPresetNames(presetFolderBox.Text);
            presetCombo.Items.Clear();
            presetCombo.Items.AddRange(names);
            SelectPreset(selectedName);
            UpdatePresetStatus();
        }

        private void SelectPreset(string presetName)
        {
            if (presetCombo == null)
                return;

            int index = -1;
            for (int i = 0; i < presetCombo.Items.Count; i++)
            {
                if (string.Equals(presetCombo.Items[i].ToString(), presetName, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            presetCombo.SelectedIndex = index;
        }

        private void UpdatePresetStatus()
        {
            if (presetStatus == null || presetFolderBox == null)
                return;

            string folder = string.IsNullOrWhiteSpace(presetFolderBox.Text) ? DraftingSuiteSettings.DefaultPresetFolderPath : presetFolderBox.Text.Trim();
            string selected = string.IsNullOrWhiteSpace(presetCombo.Text) ? "none selected" : presetCombo.Text;
            string defaultText = string.IsNullOrWhiteSpace(defaultPresetName) ? "none" : defaultPresetName;
            presetStatus.Text = "Settings: " + DraftingSuiteSettings.SettingsPath + Environment.NewLine +
                                "Presets: " + folder + " | Selected: " + selected + " | Default template: " + defaultText;
        }

        private static bool TryReadDouble(TextBox box, string label, out double value)
        {
            if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;

            MessageBox.Show(label + " must be a number.", "Invalid setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            box.Focus();
            return false;
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("0.########", CultureInfo.InvariantCulture);
        }

        private static string JoinLines(System.Collections.Generic.IEnumerable<string> values)
        {
            return string.Join(Environment.NewLine, values ?? Enumerable.Empty<string>());
        }

        private static decimal ClampDecimal(int value, decimal minimum, decimal maximum)
        {
            decimal decimalValue = value;
            if (decimalValue < minimum)
                return minimum;
            if (decimalValue > maximum)
                return maximum;
            return decimalValue;
        }

        private static System.Collections.Generic.List<string> SplitLines(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }

        private static string PromptForText(string title, string label, string defaultValue)
        {
            using (Form form = new Form())
            using (Label prompt = new Label())
            using (TextBox box = new TextBox())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                form.Text = title;
                form.Width = 420;
                form.Height = 150;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                form.MaximizeBox = false;

                prompt.Text = label;
                prompt.SetBounds(12, 12, 380, 20);
                box.Text = defaultValue ?? string.Empty;
                box.SetBounds(12, 38, 380, 24);
                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(220, 74, 80, 26);
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(312, 74, 80, 26);

                form.Controls.AddRange(new Control[] { prompt, box, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                return form.ShowDialog() == DialogResult.OK ? box.Text.Trim() : string.Empty;
            }
        }
    }
}
