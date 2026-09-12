using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using CadColor = Autodesk.AutoCAD.Colors.Color;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Windows;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DraftingSuite
{
    internal static class PolylineElevationEditorPalette
    {
        private static readonly Guid PaletteGuid = new Guid("0a5a6dc9-e0f7-4d76-b29a-15e35c1ccf2e");
        private static PaletteSet paletteSet;
        private static EditorControl control;
        private static Session session;
        private static Circle marker;

        internal static void StartSession(Document document, List<Commands.PolylineVertexElevationEdit> vertices)
        {
            if (document == null || vertices == null || vertices.Count == 0)
                return;

            EnsureCreated();
            ClearMarker();
            session = new Session(document, vertices);
            paletteSet.Visible = true;
            paletteSet.Activate(0);
            control.RefreshSession();
            FocusCurrent();
        }

        private static void EnsureCreated()
        {
            if (paletteSet != null)
                return;

            paletteSet = new PaletteSet("3D Poly Z", "EDIT3DZ", PaletteGuid)
            {
                Style = PaletteSetStyles.ShowAutoHideButton |
                        PaletteSetStyles.ShowCloseButton |
                        PaletteSetStyles.ShowPropertiesMenu |
                        PaletteSetStyles.Snappable,
                MinimumSize = new Size(300, 250),
                Size = new Size(330, 330),
                DockEnabled = DockSides.Left | DockSides.Right
            };
            control = new EditorControl();
            paletteSet.Add("Editor", control);
            paletteSet.StateChanged += (_, __) =>
            {
                if (!paletteSet.Visible)
                    ClearMarker();
            };
        }

        private static bool HasActiveSession(out Document document, out Editor editor)
        {
            document = session?.Document;
            editor = document?.Editor;
            if (document == null || editor == null || AcadApplication.DocumentManager.MdiActiveDocument != document)
            {
                MessageBox.Show("Return to the drawing where 3D Poly Z was started.", "3D Poly Z", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            return session.Index >= 0 && session.Index < session.Vertices.Count;
        }

        private static void FocusCurrent()
        {
            if (!HasActiveSession(out Document document, out Editor editor))
                return;

            Commands.PolylineVertexElevationEdit active = session.Vertices[session.Index];
            if (!Commands.TryReadVertexPosition(document.Database, active.VertexId, out Point3d position))
            {
                session.Index++;
                control.RefreshSession();
                if (session.Index < session.Vertices.Count)
                    FocusCurrent();
                return;
            }

            editor.SetImpliedSelection(new[] { active.PolylineId });
            using (ViewTableRecord view = editor.GetCurrentView())
            {
                double currentHeight = Math.Max(view.Height, 1.0);
                if (session.FocusHeight <= 0.0)
                    session.FocusHeight = Math.Max(currentHeight * 0.08, 5.0);

                double aspect = Math.Max(view.Width, currentHeight) / currentHeight;
                view.Height = session.FocusHeight;
                view.Width = session.FocusHeight * aspect;
                view.CenterPoint = new Point2d(position.X, position.Y);
                editor.SetCurrentView(view);
            }

            ClearMarker();
            marker = new Circle(position, Vector3d.ZAxis, Math.Max(session.FocusHeight * 0.04, 0.25))
            {
                Color = CadColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 2)
            };
            TransientManager.CurrentTransientManager.AddTransient(
                marker,
                TransientDrawingMode.DirectShortTerm,
                128,
                new IntegerCollection());
            editor.UpdateScreen();
            control.RefreshSession();
        }

        private static void ClearMarker()
        {
            if (marker == null)
                return;

            try
            {
                TransientManager.CurrentTransientManager.EraseTransient(marker, new IntegerCollection());
                marker.Dispose();
            }
            catch
            {
            }
            finally
            {
                marker = null;
            }
        }

        private static void SetTypedElevation()
        {
            if (!HasActiveSession(out Document document, out Editor editor))
                return;

            PromptDoubleResult result = RunPrompt(() => editor.GetDouble(new PromptDoubleOptions("\nEnter vertex elevation: ")
            {
                AllowNegative = true,
                AllowZero = true
            }));
            if (result.Status != PromptStatus.OK)
                return;

            ApplyElevation(result.Value);
        }

        private static void PickElevation()
        {
            if (!HasActiveSession(out Document document, out Editor editor))
                return;

            PromptEntityOptions options = new PromptEntityOptions("\nSnap or pick a point on the source 3D polyline: ");
            options.SetRejectMessage("\nSelect a 3D polyline.");
            options.AddAllowedClass(typeof(Polyline3d), false);
            PromptEntityResult result = RunPrompt(() => editor.GetEntity(options));
            if (result.Status != PromptStatus.OK)
                return;

            if (!Commands.TryGetPickedPolyline3dElevation(document.Database, result.ObjectId, result.PickedPoint, out double elevation))
            {
                editor.WriteMessage("\n3D Poly Z could not determine an elevation at that pick.");
                return;
            }

            ApplyElevation(elevation);
        }

        private static void ApplyElevation(double elevation)
        {
            if (!HasActiveSession(out Document document, out Editor editor))
                return;

            Commands.PolylineVertexElevationEdit active = session.Vertices[session.Index];
            bool updated;
            using (document.LockDocument())
                updated = Commands.SetVertexElevation(document.Database, active.VertexId, elevation);

            if (!updated)
            {
                editor.WriteMessage("\n3D Poly Z could not update that vertex.");
                return;
            }

            session.Changed++;
            session.Index++;
            AdvanceOrFinish();
        }

        private static void KeepCurrent()
        {
            if (session == null)
                return;

            session.Index++;
            AdvanceOrFinish();
        }

        private static void Previous()
        {
            if (session == null)
                return;

            session.Index = Math.Max(0, session.Index - 1);
            FocusCurrent();
        }

        private static void AdvanceOrFinish()
        {
            if (session == null)
                return;

            if (session.Index < session.Vertices.Count)
            {
                FocusCurrent();
                return;
            }

            Finish();
        }

        private static void Finish()
        {
            if (session?.Document?.Editor != null)
            {
                session.Document.Editor.SetImpliedSelection(new ObjectId[0]);
                session.Document.Editor.WriteMessage("\n3D Poly Z complete. Updated {0} vertex elevation(s).", session.Changed);
                session.Document.Editor.WriteMessage("\n");
            }

            ClearMarker();
            session = null;
            control.RefreshSession();
            if (paletteSet != null)
                paletteSet.Visible = false;
        }

        private static T RunPrompt<T>(Func<T> prompt)
        {
            bool wasVisible = paletteSet != null && paletteSet.Visible;
            if (wasVisible)
                paletteSet.Visible = false;

            try
            {
                return prompt();
            }
            finally
            {
                if (wasVisible && paletteSet != null)
                {
                    paletteSet.Visible = true;
                    paletteSet.Activate(0);
                }
            }
        }

        private sealed class Session
        {
            public Session(Document document, List<Commands.PolylineVertexElevationEdit> vertices)
            {
                Document = document;
                Vertices = vertices;
            }

            public Document Document { get; }
            public List<Commands.PolylineVertexElevationEdit> Vertices { get; }
            public int Index { get; set; }
            public int Changed { get; set; }
            public double FocusHeight { get; set; }
        }

        private sealed class EditorControl : UserControl
        {
            private readonly Label currentLabel;
            private readonly Label positionLabel;
            private readonly Button elevationButton;
            private readonly Button pickButton;
            private readonly Button keepButton;
            private readonly Button previousButton;
            private readonly Button finishButton;

            public EditorControl()
            {
                BackColor = Color.White;
                Dock = DockStyle.Fill;
                Font = new System.Drawing.Font("Segoe UI", 8.5f);

                TableLayoutPanel root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 4,
                    Padding = new Padding(12)
                };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                currentLabel = new Label
                {
                    AutoSize = true,
                    Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold),
                    ForeColor = Color.FromArgb(30, 64, 175),
                    Margin = new Padding(0, 0, 0, 5)
                };
                positionLabel = new Label
                {
                    AutoSize = true,
                    ForeColor = Color.FromArgb(55, 65, 81),
                    Margin = new Padding(0, 0, 0, 12)
                };

                FlowLayoutPanel actions = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
                    WrapContents = true
                };
                elevationButton = ActionButton("Set Elevation");
                elevationButton.Click += (_, __) => SetTypedElevation();
                pickButton = ActionButton("Pick Elevation");
                pickButton.Click += (_, __) => PickElevation();
                keepButton = ActionButton("Keep");
                keepButton.Click += (_, __) => KeepCurrent();
                previousButton = ActionButton("Previous");
                previousButton.Click += (_, __) => Previous();
                finishButton = ActionButton("Finish");
                finishButton.Click += (_, __) => Finish();

                actions.Controls.Add(elevationButton);
                actions.Controls.Add(pickButton);
                actions.Controls.Add(keepButton);
                actions.Controls.Add(previousButton);
                actions.Controls.Add(finishButton);

                Label hint = new Label
                {
                    Text = "The yellow ring marks the active vertex. Pick Elevation honors object snaps and interpolates along the selected source 3D polyline.",
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    ForeColor = Color.FromArgb(75, 85, 99),
                    Padding = new Padding(0, 12, 0, 0)
                };

                root.Controls.Add(currentLabel, 0, 0);
                root.Controls.Add(positionLabel, 0, 1);
                root.Controls.Add(actions, 0, 2);
                root.Controls.Add(hint, 0, 3);
                Controls.Add(root);
                RefreshSession();
            }

            internal void RefreshSession()
            {
                bool active = session != null && session.Index >= 0 && session.Index < session.Vertices.Count;
                elevationButton.Enabled = active;
                pickButton.Enabled = active;
                keepButton.Enabled = active;
                previousButton.Enabled = active && session.Index > 0;
                finishButton.Enabled = active;

                if (!active)
                {
                    currentLabel.Text = "No active 3D Poly Z session.";
                    positionLabel.Text = "Run EDIT3DZ, then select one or more 3D polylines.";
                    return;
                }

                Commands.PolylineVertexElevationEdit item = session.Vertices[session.Index];
                currentLabel.Text = "Polyline " + item.Handle + " · Vertex " + item.VertexNumber + " · " + (session.Index + 1) + " of " + session.Vertices.Count;
                if (Commands.TryReadVertexPosition(session.Document.Database, item.VertexId, out Point3d point))
                    positionLabel.Text = string.Format("X {0:0.###}   Y {1:0.###}   Z {2:0.###}", point.X, point.Y, point.Z);
                else
                    positionLabel.Text = "This vertex is no longer available.";
            }

            private static Button ActionButton(string text)
            {
                return new Button
                {
                    Text = text,
                    AutoSize = true,
                    Height = 28,
                    Margin = new Padding(0, 0, 6, 6),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(248, 250, 252),
                    ForeColor = Color.FromArgb(31, 41, 55)
                };
            }
        }
    }
}
