using System;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DraftingSuite
{
    internal sealed class DraftingSuiteSettingsForm : Form
    {
        private CheckBox extractCogoCheck;
        private CheckBox convertTextCheck;
        private CheckBox flattenCheck;
        private CheckBox restyleCheck;
        private TextBox offsetXBox;
        private TextBox offsetYBox;
        private TextBox flattenElevationBox;
        private TextBox pointStyleBox;
        private TextBox labelStyleBox;
        private TextBox protectedLayersBox;
        private TextBox resultLayersBox;
        private TextBox annotationLayersBox;
        private TextBox tinyTextBox;
        private NumericUpDown explodeBeforeBox;
        private CheckBox burstCheck;
        private NumericUpDown explodeAfterBox;
        private NumericUpDown maxAnonymousBurstPassesBox;

        private DraftingSuiteSettingsForm()
        {
            Text = "Drafting Suite Settings";
            Width = 520;
            Height = 650;
            MinimumSize = new System.Drawing.Size(460, 560);
            StartPosition = FormStartPosition.CenterScreen;

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            TableLayoutPanel fields = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                ColumnCount = 2
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            extractCogoCheck = AddCheck(fields, "Extract COGO graphics");
            convertTextCheck = AddCheck(fields, "Convert text to mleaders");
            flattenCheck = AddCheck(fields, "Flatten annotation");
            restyleCheck = AddCheck(fields, "Restyle COGO points");
            offsetXBox = AddText(fields, "MLeader offset X");
            offsetYBox = AddText(fields, "MLeader offset Y");
            flattenElevationBox = AddText(fields, "Flatten elevation");
            pointStyleBox = AddText(fields, "COGO point style");
            labelStyleBox = AddText(fields, "COGO label style");
            protectedLayersBox = AddMultiline(fields, "Protected source layers");
            resultLayersBox = AddMultiline(fields, "Result layers to keep");
            annotationLayersBox = AddMultiline(fields, "Annotation layers to keep");
            tinyTextBox = AddText(fields, "Tiny text max height");
            explodeBeforeBox = AddNumber(fields, "Explode passes before burst");
            burstCheck = AddCheck(fields, "Burst anonymous blocks");
            maxAnonymousBurstPassesBox = AddNumber(fields, "Max anonymous burst passes");
            explodeAfterBox = AddNumber(fields, "Explode passes after burst");

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

            root.Controls.Add(fields, 0, 0);
            root.Controls.Add(buttons, 0, 1);
            Controls.Add(root);

            LoadSettings(DraftingSuiteSettings.Load());
        }

        public static void ShowSettingsDialog()
        {
            using (DraftingSuiteSettingsForm form = new DraftingSuiteSettingsForm())
            {
                AcadApplication.ShowModalDialog(form);
            }
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
            offsetXBox.Text = FormatDouble(settings.MLeaderTextOffsetX);
            offsetYBox.Text = FormatDouble(settings.MLeaderTextOffsetY);
            flattenElevationBox.Text = FormatDouble(settings.FlattenElevation);
            pointStyleBox.Text = settings.CogoPointStyleName;
            labelStyleBox.Text = settings.CogoLabelStyleName;
            protectedLayersBox.Text = JoinLines(settings.ProtectedSourceLayerPatterns);
            resultLayersBox.Text = JoinLines(settings.ResultLayerPatterns);
            annotationLayersBox.Text = JoinLines(settings.AnnotationLayerPatterns);
            tinyTextBox.Text = FormatDouble(settings.TinyTextDeleteHeight);
            explodeBeforeBox.Value = ClampDecimal(settings.ExplodePassesBeforeBurst, explodeBeforeBox.Minimum, explodeBeforeBox.Maximum);
            burstCheck.Checked = settings.BurstInserts;
            maxAnonymousBurstPassesBox.Value = ClampDecimal(settings.MaxAnonymousBurstPasses, maxAnonymousBurstPassesBox.Minimum, maxAnonymousBurstPassesBox.Maximum);
            explodeAfterBox.Value = ClampDecimal(settings.ExplodePassesAfterBurst, explodeAfterBox.Minimum, explodeAfterBox.Maximum);
        }

        private void SaveAndClose()
        {
            if (!TryReadDouble(offsetXBox, "MLeader offset X", out double offsetX) ||
                !TryReadDouble(offsetYBox, "MLeader offset Y", out double offsetY) ||
                !TryReadDouble(flattenElevationBox, "Flatten elevation", out double flattenElevation) ||
                !TryReadDouble(tinyTextBox, "Tiny text max height", out double tinyTextHeight))
            {
                return;
            }

            DraftingSuiteSettings settings = new DraftingSuiteSettings
            {
                ExtractCogoDisplayGraphics = extractCogoCheck.Checked,
                ConvertTextToMleaders = convertTextCheck.Checked,
                FlattenAnnotation = flattenCheck.Checked,
                RestyleCogoPoints = restyleCheck.Checked,
                MLeaderTextOffsetX = offsetX,
                MLeaderTextOffsetY = offsetY,
                FlattenElevation = flattenElevation,
                CogoPointStyleName = pointStyleBox.Text.Trim(),
                CogoLabelStyleName = labelStyleBox.Text.Trim(),
                ProtectedSourceLayerPatterns = SplitLines(protectedLayersBox.Text),
                ResultLayerPatterns = SplitLines(resultLayersBox.Text),
                AnnotationLayerPatterns = SplitLines(annotationLayersBox.Text),
                TinyTextDeleteHeight = tinyTextHeight,
                ExplodePassesBeforeBurst = (int)explodeBeforeBox.Value,
                BurstInserts = burstCheck.Checked,
                MaxAnonymousBurstPasses = (int)maxAnonymousBurstPassesBox.Value,
                ExplodePassesAfterBurst = (int)explodeAfterBox.Value
            };

            try
            {
                settings.Save();
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Settings could not be saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
    }
}
