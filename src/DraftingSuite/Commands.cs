using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using WinForms = System.Windows.Forms;

namespace DraftingSuite
{
    public sealed class PluginEntry : IExtensionApplication
    {
        public void Initialize()
        {
            Commands.PrintLoadMessage(Application.DocumentManager.MdiActiveDocument?.Editor);
        }

        public void Terminate()
        {
        }
    }

    public sealed class Commands
    {
        private const string Version = "0.1.73";
        private const string CfbkDictionaryName = "DRAFTING_SUITE_CFBK";
        private const string CfbkImportSchema = "DraftingSuite.CFBK.Import.v1";
        private const string ScanGridLayerName = "0_grid";

        internal static string VersionText => Version;

        [CommandMethod("DS", CommandFlags.Session)]
        public void OpenPalette()
        {
            DraftingSuitePalette.ShowPalette();
        }

        [CommandMethod("DSFBKPREP", CommandFlags.Modal)]
        public void PrepareFieldbook()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc?.Editor;
            if (doc == null || ed == null)
                return;

            try
            {
                FbkPrepScope scope = PromptScope(ed);
                if (scope == FbkPrepScope.Canceled)
                {
                    ed.WriteMessage("\nFBK Prep canceled.");
                    ed.WriteMessage("\n");
                    return;
                }

                DraftingSuiteSettings settings = DraftingSuiteSettings.Load();
                using (doc.LockDocument())
                {
                    FbkPrepResult result = RunFbkPrep(doc.Database, ed, scope, settings);
                    ed.WriteMessage("\nFBK Prep complete.");
                    ed.WriteMessage("\n  Scope: {0}", scope == FbkPrepScope.Selection ? "selection" : "entire drawing");
                    if (!string.IsNullOrWhiteSpace(settings.PresetName))
                        ed.WriteMessage("\n  Preset: {0}", settings.PresetName);
                    ed.WriteMessage("\n  COGO points found: {0}", result.CogoPointsFound);
                    ed.WriteMessage("\n  COGO display objects created: {0}", result.CogoDisplayObjectsCreated);
                    ed.WriteMessage("\n  Survey networks deleted: {0}", result.SurveyNetworksDeleted);
                    ed.WriteMessage("\n  Block references exploded: {0}", result.BlockReferencesExploded);
                    ed.WriteMessage("\n  Anonymous blocks burst: {0}", result.AnonymousBlocksBurst);
                    ed.WriteMessage("\n  COGO-layer lines deleted: {0}", result.CogoLayerLinesDeleted);
                    ed.WriteMessage("\n  Lines converted to 3D polylines: {0}", result.LinesConvertedTo3dPolylines);
                    ed.WriteMessage("\n  Small text deleted: {0}", result.TinyTextDeleted);
                    ed.WriteMessage("\n  Text/MText deleted by layer: {0}", result.TextDeletedByLayer);
                    ed.WriteMessage("\n  Text/MText kept by layer: {0}", result.TextKeptByLayer);
                    ed.WriteMessage("\n  Text/MText converted to MLeaders: {0}", result.TextConvertedToMleaders);
                    ed.WriteMessage("\n  Annotation objects flattened: {0}", result.AnnotationObjectsFlattened);
                    ed.WriteMessage("\n  Blocks flattened: {0}", result.BlocksFlattened);
                    ed.WriteMessage("\n  Blocks skipped by flatten rule: {0}", result.BlocksSkippedByFlattenRule);
                    ed.WriteMessage("\n  Objects set to ByLayer: {0}", result.ObjectsSetByLayer);
                    ed.WriteMessage("\n  COGO points restyled: {0}", result.CogoPointsRestyled);
                    if (result.CogoPointStyleSkipped > 0)
                        ed.WriteMessage("\n  COGO style changes skipped: {0}", result.CogoPointStyleSkipped);
                    if (result.Errors.Count > 0)
                    {
                        ed.WriteMessage("\n  Warnings:");
                        foreach (string error in result.Errors.Take(8))
                            ed.WriteMessage("\n    {0}", error);
                        if (result.Errors.Count > 8)
                            ed.WriteMessage("\n    {0} more warning(s).", result.Errors.Count - 8);
                    }

                    ed.WriteMessage("\n");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nFBK Prep failed: {0}", ex.Message);
                ed.WriteMessage("\n");
            }
        }

        [CommandMethod("CFBK", CommandFlags.Session)]
        public void CombineFieldbookDrawings()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc?.Editor;
            if (doc == null || ed == null)
                return;

            try
            {
                string sourceFolder = PromptCfbkSourceFolder(doc);
                if (string.IsNullOrWhiteSpace(sourceFolder))
                {
                    ed.WriteMessage("\nCFBK canceled.");
                    ed.WriteMessage("\n");
                    return;
                }

                string filter = PromptCfbkFilter(ed);
                if (filter == null)
                {
                    ed.WriteMessage("\nCFBK canceled.");
                    ed.WriteMessage("\n");
                    return;
                }

                CfbkSourcePlan sourcePlan = FindCfbkSourceDrawings(sourceFolder, filter, doc.Database.Filename);
                if (sourcePlan.MatchingDrawings.Count == 0)
                {
                    ed.WriteMessage("\nCFBK found no drawings matching {0}.", filter);
                    ed.WriteMessage("\n");
                    return;
                }

                Dictionary<string, CfbkImportRecord> importRecords = ReadCfbkImportRecords(doc.Database);
                bool importCogoPoints = PromptCfbkImportCogoPoints();
                if (!ConfirmCfbkRun(sourceFolder, filter, sourcePlan.MatchingDrawings.Count, importRecords.Count, importCogoPoints))
                {
                    ed.WriteMessage("\nCFBK canceled.");
                    ed.WriteMessage("\n");
                    return;
                }

                CfbkRunResult result = CreateCfbkRunResult(sourceFolder, filter, importCogoPoints);
                AddIgnoredCfbkDrawings(result, sourcePlan.IgnoredDrawings);
                using (doc.LockDocument())
                {
                    foreach (string sourcePath in sourcePlan.MatchingDrawings)
                    {
                        CfbkImportedDrawing drawing = new CfbkImportedDrawing(sourcePath);
                        result.Drawings.Add(drawing);
                        ApplyCfbkFileSignature(drawing);
                        CfbkImportRecord existingRecord;
                        if (importRecords.TryGetValue(NormalizeCfbkPath(sourcePath), out existingRecord))
                        {
                            drawing.PreviousImportUtc = existingRecord.ImportedUtc;
                            if (!existingRecord.CanRefresh)
                            {
                                drawing.SkipReason = "Already imported by legacy record";
                                drawing.DestinationHandles.AddRange(existingRecord.DestinationHandles);
                                ed.WriteMessage("\n  Skipped {0}: already imported by a record without refresh handles.", Path.GetFileName(sourcePath));
                                continue;
                            }

                            if (existingRecord.Matches(drawing.FileSizeBytes, drawing.LastWriteUtc))
                            {
                                drawing.SkipReason = "Already imported and unchanged";
                                drawing.DestinationHandles.AddRange(existingRecord.DestinationHandles);
                                ed.WriteMessage("\n  Skipped {0}: already imported and unchanged.", Path.GetFileName(sourcePath));
                                continue;
                            }

                            drawing.WasReimported = true;
                            drawing.DeletedPreviousEntityCount = DeleteCfbkImportedObjects(doc.Database, existingRecord);
                            ed.WriteMessage(
                                "\n  Reimporting {0}: source changed, erased {1} previous object(s).",
                                Path.GetFileName(sourcePath),
                                drawing.DeletedPreviousEntityCount);
                        }

                        try
                        {
                            CloneAllowedModelSpaceFromDrawing(doc.Database, sourcePath, drawing, importCogoPoints);
                            RegisterCfbkImport(doc.Database, drawing);
                            ed.WriteMessage(
                                "\n  Imported {0}: {1} object(s), skipped {2}, COGO imported {3}, duplicate COGO {4}",
                                Path.GetFileName(sourcePath),
                                drawing.ImportedEntityCount,
                                drawing.SkippedEntityCount,
                                drawing.ImportedCogoPointCount,
                                drawing.DuplicateCogoPointCount);
                        }
                        catch (System.Exception ex)
                        {
                            drawing.ImportError = ex.Message;
                            ed.WriteMessage("\n  Import failed for {0}: {1}", Path.GetFileName(sourcePath), ex.Message);
                        }
                    }

                    InsertCfbkModelSpaceReport(doc.Database, ed, result);
                }

                WriteCfbkSummary(result);
                ed.WriteMessage("\nCFBK complete.");
                ed.WriteMessage("\n  Matching source drawings: {0}", sourcePlan.MatchingDrawings.Count);
                ed.WriteMessage("\n  Imported drawings: {0}", result.ImportedDrawingCount);
                ed.WriteMessage("\n  Reimported changed drawings: {0}", result.ReimportedDrawingCount);
                ed.WriteMessage("\n  Already imported and unchanged: {0}", result.AlreadyImportedCount);
                ed.WriteMessage("\n  Legacy import records skipped: {0}", result.LegacyImportRecordCount);
                ed.WriteMessage("\n  Import failures: {0}", result.ImportFailureCount);
                ed.WriteMessage("\n  Ignored or filtered drawings: {0}", result.IgnoredDrawingCount);
                ed.WriteMessage("\n  Previous objects erased: {0}", result.DeletedPreviousEntityCount);
                ed.WriteMessage("\n  Objects imported: {0}", result.ImportedEntityCount);
                ed.WriteMessage("\n  Objects skipped: {0}", result.SkippedEntityCount);
                ed.WriteMessage("\n  COGO points imported: {0}", result.ImportedCogoPointCount);
                ed.WriteMessage("\n  Duplicate COGO points skipped: {0}", result.DuplicateCogoPointCount);
                ed.WriteMessage("\n  COGO point import failures: {0}", result.CogoPointImportFailureCount);
                ed.WriteMessage("\n  Log folder: {0}", result.LogFolder);
                ed.WriteMessage("\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nCFBK failed: {0}", ex.Message);
                ed.WriteMessage("\n");
            }
        }

        [CommandMethod("DSVERSION", CommandFlags.Modal)]
        public void VersionInfo()
        {
            PrintLoadMessage(Application.DocumentManager.MdiActiveDocument?.Editor);
        }

        [CommandMethod("DSFBKCONFIG", CommandFlags.Session)]
        public void OpenFbkPrepConfig()
        {
            DraftingSuiteSettingsForm.ShowSettingsDialog();
        }

        [CommandMethod("DSSETTINGS", CommandFlags.Session)]
        public void OpenSettings()
        {
            DraftingSuiteSettingsForm.ShowSettingsDialog();
        }

        [CommandMethod("DSMT2ML", CommandFlags.Modal)]
        public void ConvertSelectedTextToMleaders()
        {
            RunSelectionUtility(
                "Text to MLeader",
                "\nSelect text or mtext to convert to mleaders: ",
                (db, tr, ids, result, settings) => ConvertTextToMleaders(db, tr, ids, result, CreateStandaloneUtilitySettings(), false, true, true),
                (ed, result) => ed.WriteMessage("\n  Text/MText converted to MLeaders: {0}", result.TextConvertedToMleaders));
        }

        [CommandMethod("DSDELETETINY", CommandFlags.Modal)]
        public void DeleteSelectedTinyText()
        {
            RunSelectionUtility(
                "Delete Small Text",
                "\nSelect text or mtext to check for small text deletion: ",
                (db, tr, ids, result, settings) => DeleteTinyText(tr, ids, result, settings),
                (ed, result) => ed.WriteMessage("\n  Small text deleted: {0}", result.TinyTextDeleted));
        }

        [CommandMethod("DSFLATTEN", CommandFlags.Modal)]
        public void FlattenSelectedAnnotation()
        {
            RunSelectionUtility(
                "Flatten Annotation",
                "\nSelect drafting annotation to flatten: ",
                (db, tr, ids, result, settings) => FlattenObjects(tr, ids, result, CreateStandaloneUtilitySettings(), true),
                (ed, result) =>
                {
                    ed.WriteMessage("\n  Objects flattened: {0}", result.AnnotationObjectsFlattened + result.BlocksFlattened);
                });
        }

        [CommandMethod("DSBYLAYER", CommandFlags.Modal)]
        public void SetSelectedObjectsByLayer()
        {
            RunSelectionUtility(
                "ByLayer Cleanup",
                "\nSelect objects to set to ByLayer: ",
                (db, tr, ids, result, settings) => ApplyByLayerToEntities(tr, ids, result),
                (ed, result) => ed.WriteMessage("\n  Objects set to ByLayer: {0}", result.ObjectsSetByLayer));
        }

        [CommandMethod("DSLINE3D", CommandFlags.Modal)]
        public void ConvertSelectedLinesTo3dPolylines()
        {
            RunSelectionUtility(
                "Lines to 3D Polylines",
                "\nSelect lines to convert to 3D polylines: ",
                (db, tr, ids, result, settings) => ConvertLinesTo3dPolylines(tr, ids, result, CreateStandaloneUtilitySettings()),
                (ed, result) =>
                {
                    ed.WriteMessage("\n  Lines converted to 3D polylines: {0}", result.LinesConvertedTo3dPolylines);
                });
        }

        [CommandMethod("DSCOGOSTD", CommandFlags.Modal)]
        public void SetSelectedCogoPointsStandard()
        {
            RunSelectionUtility(
                "COGO Standard",
                "\nSelect COGO points to restyle: ",
                (db, tr, ids, result, settings) =>
                {
                    List<ObjectId> cogoIds = ids
                        .Where(id => IsCogoPoint(GetDBObjectOrNull(tr, id)))
                        .ToList();
                    result.CogoPointsFound = cogoIds.Count;
                    RestyleCogoPoints(tr, cogoIds, result, CreateStandaloneUtilitySettings());
                },
                (ed, result) =>
                {
                    ed.WriteMessage("\n  COGO points found: {0}", result.CogoPointsFound);
                    ed.WriteMessage("\n  COGO points restyled: {0}", result.CogoPointsRestyled);
                    if (result.CogoPointStyleSkipped > 0)
                        ed.WriteMessage("\n  COGO style changes skipped: {0}", result.CogoPointStyleSkipped);
                });
        }

        [CommandMethod("DSGRID", CommandFlags.Modal)]
        public void CreateScanGrid()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc?.Editor;
            if (doc == null || ed == null)
                return;

            try
            {
                PromptEntityOptions boundaryOptions = new PromptEntityOptions("\nSelect closed grid boundary polyline: ");
                boundaryOptions.SetRejectMessage("\nSelect a lightweight closed polyline.");
                boundaryOptions.AddAllowedClass(typeof(Polyline), false);
                PromptEntityResult boundaryResult = ed.GetEntity(boundaryOptions);
                if (boundaryResult.Status != PromptStatus.OK)
                    return;

                double spacing = PromptScanGridSpacing(ed);
                if (spacing <= 0.0)
                    return;

                short colorIndex = PromptScanGridColor(ed);
                if (colorIndex <= 0)
                    return;

                double rotation = PromptScanGridRotation(ed);
                if (double.IsNaN(rotation))
                    return;

                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    Polyline boundary = tr.GetObject(boundaryResult.ObjectId, OpenMode.ForRead, false) as Polyline;
                    if (boundary == null)
                    {
                        ed.WriteMessage("\nScan grid canceled: selected object is not a lightweight polyline.");
                        return;
                    }

                    ScanGridBoundary gridBoundary = ReadScanGridBoundary(boundary);
                    ObjectId layerId = EnsureScanGridLayer(doc.Database, tr, colorIndex);
                    List<ScanGridSegment> segments = BuildScanGridSegments(gridBoundary.Points, spacing, rotation);

                    BlockTableRecord currentSpace = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                    foreach (ScanGridSegment segment in segments)
                    {
                        Polyline line = new Polyline(2);
                        line.SetDatabaseDefaults();
                        line.LayerId = layerId;
                        line.ColorIndex = 256;
                        line.Linetype = "ByLayer";
                        line.Elevation = gridBoundary.Elevation;
                        line.AddVertexAt(0, segment.Start, 0.0, 0.0, 0.0);
                        line.AddVertexAt(1, segment.End, 0.0, 0.0, 0.0);
                        currentSpace.AppendEntity(line);
                        tr.AddNewlyCreatedDBObject(line, true);
                    }

                    tr.Commit();
                    ed.WriteMessage("\nScan grid complete.");
                    ed.WriteMessage("\n  Layer: {0}", ScanGridLayerName);
                    ed.WriteMessage("\n  Spacing: {0}", spacing.ToString("0.###", CultureInfo.InvariantCulture));
                    ed.WriteMessage("\n  Segments created: {0}", segments.Count);
                    ed.WriteMessage("\n");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nScan grid failed: {0}", ex.Message);
                ed.WriteMessage("\n");
            }
        }

        internal static void PrintLoadMessage(Editor ed)
        {
            if (ed == null)
                return;

            ed.WriteMessage("\nDrafting Suite v{0} loaded. Commands: DS, CFBK, DSGRID, DSFBKPREP, DSFBKCONFIG, DSMT2ML, DSDELETETINY, DSFLATTEN, DSBYLAYER, DSLINE3D, DSCOGOSTD, DSSETTINGS, DSVERSION.", Version);
            ed.WriteMessage("\n");
        }

        private static DraftingSuiteSettings CreateStandaloneUtilitySettings()
        {
            DraftingSuiteSettings settings = DraftingSuiteSettings.CreateDefault();
            settings.PresetName = string.Empty;
            settings.MLeaderTextOffsetX = 15.0;
            settings.MLeaderTextOffsetY = 15.0;
            settings.FlattenElevation = 0.0;
            settings.CogoPointStyleName = "Standard";
            settings.CogoLabelStyleName = "Standard";
            settings.ProtectedSourceLayerPatterns = new List<string>();
            settings.MLeaderIgnoreLayerPatterns = new List<string>();
            settings.MLeaderDeleteLayerPatterns = new List<string>();
            settings.MLeaderKeepTextLayerPatterns = new List<string>();
            settings.FlattenSkipBlockNamePatterns = new List<string>();
            settings.InvertKeepTextLayerPatterns = false;
            return settings;
        }

        private static double PromptScanGridSpacing(Editor ed)
        {
            PromptStringOptions options = new PromptStringOptions("\nGrid spacing [10/15/20/25/50]: ")
            {
                AllowSpaces = false,
                DefaultValue = "10",
                UseDefaultValue = true
            };

            PromptResult result = ed.GetString(options);
            if (result.Status == PromptStatus.Cancel)
                return -1.0;

            string text = string.IsNullOrWhiteSpace(result.StringResult) ? "10" : result.StringResult.Trim();
            double spacing;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out spacing) ||
                !IsAllowedScanGridSpacing(spacing))
            {
                ed.WriteMessage("\nSpacing must be 10, 15, 20, 25, or 50.");
                return -1.0;
            }

            return spacing;
        }

        private static bool IsAllowedScanGridSpacing(double spacing)
        {
            return Math.Abs(spacing - 10.0) < 1e-8 ||
                   Math.Abs(spacing - 15.0) < 1e-8 ||
                   Math.Abs(spacing - 20.0) < 1e-8 ||
                   Math.Abs(spacing - 25.0) < 1e-8 ||
                   Math.Abs(spacing - 50.0) < 1e-8;
        }

        private static short PromptScanGridColor(Editor ed)
        {
            PromptKeywordOptions options = new PromptKeywordOptions("\nGrid color [White/Cyan/Red/Gray/Magenta]: ");
            options.AllowNone = true;
            options.Keywords.Add("White");
            options.Keywords.Add("Cyan");
            options.Keywords.Add("Red");
            options.Keywords.Add("Gray");
            options.Keywords.Add("Magenta");
            options.Keywords.Default = "White";

            PromptResult result = ed.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return -1;

            string color = string.IsNullOrWhiteSpace(result.StringResult) ? "White" : result.StringResult;
            if (string.Equals(color, "Cyan", StringComparison.OrdinalIgnoreCase))
                return 4;
            if (string.Equals(color, "Red", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(color, "Gray", StringComparison.OrdinalIgnoreCase))
                return 8;
            if (string.Equals(color, "Magenta", StringComparison.OrdinalIgnoreCase))
                return 6;
            return 7;
        }

        private static double PromptScanGridRotation(Editor ed)
        {
            PromptAngleOptions options = new PromptAngleOptions("\nGrid rotation angle: ")
            {
                AllowNone = true,
                DefaultValue = 0.0,
                UseDefaultValue = true
            };

            PromptDoubleResult result = ed.GetAngle(options);
            if (result.Status == PromptStatus.Cancel)
                return double.NaN;

            return result.Value;
        }

        private static ObjectId EnsureScanGridLayer(Database db, Transaction tr, short colorIndex)
        {
            LayerTable layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            ObjectId layerId;
            if (layerTable.Has(ScanGridLayerName))
            {
                layerId = layerTable[ScanGridLayerName];
                LayerTableRecord existing = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForWrite);
                existing.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
                existing.LinetypeObjectId = GetContinuousLinetypeId(db, tr);
                return layerId;
            }

            layerTable.UpgradeOpen();
            LayerTableRecord layer = new LayerTableRecord
            {
                Name = ScanGridLayerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
                LinetypeObjectId = GetContinuousLinetypeId(db, tr)
            };
            layerId = layerTable.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return layerId;
        }

        private static ObjectId GetContinuousLinetypeId(Database db, Transaction tr)
        {
            LinetypeTable linetypeTable = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            return linetypeTable.Has("Continuous") ? linetypeTable["Continuous"] : db.ContinuousLinetype;
        }

        private static ScanGridBoundary ReadScanGridBoundary(Polyline boundary)
        {
            if (!boundary.Closed)
                throw new InvalidOperationException("Boundary polyline must be closed.");
            if (boundary.NumberOfVertices < 3)
                throw new InvalidOperationException("Boundary polyline needs at least three vertices.");

            List<Point2d> points = new List<Point2d>();
            for (int i = 0; i < boundary.NumberOfVertices; i++)
            {
                if (Math.Abs(boundary.GetBulgeAt(i)) > 1e-10)
                    throw new InvalidOperationException("Arc segments are not supported yet. Use a straight-segment closed polyline.");

                points.Add(boundary.GetPoint2dAt(i));
            }

            return new ScanGridBoundary(points, boundary.Elevation);
        }

        private static List<ScanGridSegment> BuildScanGridSegments(List<Point2d> worldPoints, double spacing, double rotation)
        {
            double cos = Math.Cos(rotation);
            double sin = Math.Sin(rotation);
            List<Point2d> localPoints = worldPoints
                .Select(point => WorldToScanGridLocal(point, cos, sin))
                .ToList();

            double minX = localPoints.Min(point => point.X);
            double maxX = localPoints.Max(point => point.X);
            double minY = localPoints.Min(point => point.Y);
            double maxY = localPoints.Max(point => point.Y);

            List<ScanGridSegment> segments = new List<ScanGridSegment>();
            double startX = Math.Floor(minX / spacing) * spacing;
            double endX = Math.Ceiling(maxX / spacing) * spacing;
            for (double x = startX; x <= endX + 1e-8; x += spacing)
                AddScanGridSegmentsForVerticalLine(segments, localPoints, x, cos, sin, spacing);

            double startY = Math.Floor(minY / spacing) * spacing;
            double endY = Math.Ceiling(maxY / spacing) * spacing;
            for (double y = startY; y <= endY + 1e-8; y += spacing)
                AddScanGridSegmentsForHorizontalLine(segments, localPoints, y, cos, sin, spacing);

            return segments;
        }

        private static void AddScanGridSegmentsForVerticalLine(List<ScanGridSegment> segments, List<Point2d> polygon, double x, double cos, double sin, double spacing)
        {
            List<double> intersections = new List<double>();
            const double epsilon = 1e-8;
            for (int i = 0; i < polygon.Count; i++)
            {
                Point2d a = polygon[i];
                Point2d b = polygon[(i + 1) % polygon.Count];
                double dx = b.X - a.X;
                if (Math.Abs(dx) < epsilon)
                    continue;

                if ((a.X <= x && b.X > x) || (b.X <= x && a.X > x))
                {
                    double t = (x - a.X) / dx;
                    intersections.Add(a.Y + (b.Y - a.Y) * t);
                }
            }

            AddPairedScanGridSegments(segments, intersections, true, x, cos, sin, spacing);
        }

        private static void AddScanGridSegmentsForHorizontalLine(List<ScanGridSegment> segments, List<Point2d> polygon, double y, double cos, double sin, double spacing)
        {
            List<double> intersections = new List<double>();
            const double epsilon = 1e-8;
            for (int i = 0; i < polygon.Count; i++)
            {
                Point2d a = polygon[i];
                Point2d b = polygon[(i + 1) % polygon.Count];
                double dy = b.Y - a.Y;
                if (Math.Abs(dy) < epsilon)
                    continue;

                if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                {
                    double t = (y - a.Y) / dy;
                    intersections.Add(a.X + (b.X - a.X) * t);
                }
            }

            AddPairedScanGridSegments(segments, intersections, false, y, cos, sin, spacing);
        }

        private static void AddPairedScanGridSegments(List<ScanGridSegment> segments, List<double> intersections, bool vertical, double fixedCoordinate, double cos, double sin, double spacing)
        {
            intersections.Sort();
            for (int i = 0; i + 1 < intersections.Count; i += 2)
            {
                double start = intersections[i];
                double end = intersections[i + 1];
                if (Math.Abs(end - start) < Math.Max(1e-7, spacing * 1e-7))
                    continue;

                Point2d localStart = vertical ? new Point2d(fixedCoordinate, start) : new Point2d(start, fixedCoordinate);
                Point2d localEnd = vertical ? new Point2d(fixedCoordinate, end) : new Point2d(end, fixedCoordinate);
                segments.Add(new ScanGridSegment(
                    ScanGridLocalToWorld(localStart, cos, sin),
                    ScanGridLocalToWorld(localEnd, cos, sin)));
            }
        }

        private static Point2d WorldToScanGridLocal(Point2d point, double cos, double sin)
        {
            return new Point2d(
                point.X * cos + point.Y * sin,
                -point.X * sin + point.Y * cos);
        }

        private static Point2d ScanGridLocalToWorld(Point2d point, double cos, double sin)
        {
            return new Point2d(
                point.X * cos - point.Y * sin,
                point.X * sin + point.Y * cos);
        }

        private static string PromptCfbkSourceFolder(Document doc)
        {
            string initialFolder = null;
            try
            {
                string drawingPath = doc?.Database?.Filename;
                if (!string.IsNullOrWhiteSpace(drawingPath))
                    initialFolder = Path.GetDirectoryName(drawingPath);
            }
            catch
            {
            }

            using (WinForms.FolderBrowserDialog dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = "Select the folder containing processed FBK DWGs to combine";
                if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
                    dialog.SelectedPath = initialFolder;

                return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        private static string PromptCfbkFilter(Editor ed)
        {
            PromptStringOptions options = new PromptStringOptions("\nDWG file filter <*.dwg>: ")
            {
                AllowSpaces = false
            };

            PromptResult result = ed.GetString(options);
            if (result.Status == PromptStatus.Cancel)
                return null;

            string filter = (result.StringResult ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(filter) ? "*.dwg" : filter;
        }

        private static CfbkSourcePlan FindCfbkSourceDrawings(string sourceFolder, string filter, string activeDrawingPath)
        {
            CfbkSourcePlan plan = new CfbkSourcePlan();
            if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
                return plan;
            if (string.IsNullOrWhiteSpace(filter))
                filter = "*.dwg";

            string activeFullPath = string.IsNullOrWhiteSpace(activeDrawingPath)
                ? string.Empty
                : Path.GetFullPath(activeDrawingPath);

            foreach (string path in Directory.GetFiles(sourceFolder, "*.dwg", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string fileName = Path.GetFileName(path);
                if (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
                {
                    plan.IgnoredDrawings.Add(new CfbkIgnoredDrawing(path, "Temporary DWG"));
                    continue;
                }

                if (string.Equals(Path.GetFullPath(path), activeFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    plan.IgnoredDrawings.Add(new CfbkIgnoredDrawing(path, "Active drawing"));
                    continue;
                }

                if (!MatchesWildcard(fileName, filter))
                {
                    plan.IgnoredDrawings.Add(new CfbkIgnoredDrawing(path, "Filtered out"));
                    continue;
                }

                plan.MatchingDrawings.Add(path);
            }

            return plan;
        }

        private static bool PromptCfbkImportCogoPoints()
        {
            return WinForms.MessageBox.Show(
                "Import new COGO points too?\n\nDuplicate point numbers, names, or matching rounded coordinates will be skipped. Existing destination COGO points will not be overwritten.",
                "CFBK COGO Points",
                WinForms.MessageBoxButtons.YesNo,
                WinForms.MessageBoxIcon.Question,
                WinForms.MessageBoxDefaultButton.Button2) == WinForms.DialogResult.Yes;
        }

        private static bool ConfirmCfbkRun(string sourceFolder, string filter, int drawingCount, int priorImportCount, bool importCogoPoints)
        {
            string message = string.Format(
                CultureInfo.CurrentCulture,
                "CFBK will combine allowed CAD objects from {0} matching drawing(s).\n\nFolder:\n{1}\n\nFilter:\n{2}\n\nCOGO points: {3}\n\nThis drawing already has {4} remembered CFBK import record(s). Matching unchanged files will be skipped. Matching changed files will erase their previous imported objects and import again.\n\nIt imports annotations, blocks, and linework. It skips xrefs, rasters, survey networks, underlays, point clouds, and unsupported Civil objects. The source drawings will not be modified.",
                drawingCount,
                sourceFolder,
                filter,
                importCogoPoints ? "import new points and skip duplicates" : "skip",
                priorImportCount);

            return WinForms.MessageBox.Show(
                message,
                "Run CFBK",
                WinForms.MessageBoxButtons.OKCancel,
                WinForms.MessageBoxIcon.Question) == WinForms.DialogResult.OK;
        }

        private static CfbkRunResult CreateCfbkRunResult(string sourceFolder, string filter, bool importCogoPoints)
        {
            string runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string logFolder = Path.Combine(sourceFolder, "_CFBK_Logs", runStamp);
            Directory.CreateDirectory(logFolder);

            return new CfbkRunResult { LogFolder = logFolder, SourceFolder = sourceFolder, Filter = filter, ImportCogoPoints = importCogoPoints, StartedUtc = DateTime.UtcNow };
        }

        private static void AddIgnoredCfbkDrawings(CfbkRunResult result, IEnumerable<CfbkIgnoredDrawing> ignoredDrawings)
        {
            foreach (CfbkIgnoredDrawing ignored in ignoredDrawings)
            {
                CfbkImportedDrawing drawing = new CfbkImportedDrawing(ignored.SourcePath);
                drawing.SkipReason = ignored.Reason;
                result.Drawings.Add(drawing);
            }
        }

        private static void ApplyCfbkFileSignature(CfbkImportedDrawing drawing)
        {
            FileInfo info = new FileInfo(drawing.SourcePath);
            drawing.FileSizeBytes = info.Exists ? info.Length : -1;
            drawing.LastWriteUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
        }

        private static void CloneAllowedModelSpaceFromDrawing(Database destinationDb, string sourcePath, CfbkImportedDrawing result, bool importCogoPoints)
        {
            using (Database sourceDb = new Database(false, true))
            {
                sourceDb.ReadDwgFile(sourcePath, FileShare.ReadWrite, true, string.Empty);
                sourceDb.CloseInput(true);

                ObjectIdCollection sourceIds = new ObjectIdCollection();
                HashSet<string> destinationCogoKeys = importCogoPoints ? CollectCfbkCogoKeys(destinationDb) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<CfbkCogoPointData> cogoPointsToImport = new List<CfbkCogoPointData>();
                using (Transaction tr = sourceDb.TransactionManager.StartTransaction())
                {
                    BlockTable blockTable = (BlockTable)tr.GetObject(sourceDb.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in modelSpace)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead, false);
                        if (importCogoPoints && IsCogoPoint(obj))
                        {
                            CfbkCogoPointSignature signature = ReadCfbkCogoPointSignature(obj);
                            if (HasCfbkCogoDuplicate(destinationCogoKeys, signature))
                            {
                                result.DuplicateCogoPointCount++;
                                result.SkippedEntityCount++;
                                AddCfbkSkippedObjectType(result, "Duplicate COGO point");
                                continue;
                            }

                            CfbkCogoPointData data = ReadCfbkCogoPointData(obj);
                            if (data == null)
                            {
                                result.CogoPointImportFailureCount++;
                                result.SkippedEntityCount++;
                                AddCfbkSkippedObjectType(result, "Unreadable COGO point");
                                continue;
                            }

                            cogoPointsToImport.Add(data);
                            AddCfbkCogoKeys(destinationCogoKeys, signature);
                            continue;
                        }

                        if (IsAllowedCfbkEntity(tr, id))
                            sourceIds.Add(id);
                        else
                        {
                            result.SkippedEntityCount++;
                            AddCfbkSkippedObjectType(result, ClassifyCfbkSkippedObject(tr, obj));
                        }
                    }

                    tr.Commit();
                }

                if (sourceIds.Count == 0)
                {
                    ImportCfbkCogoPoints(cogoPointsToImport, result);
                    return;
                }

                IdMapping mapping = new IdMapping();
                sourceDb.WblockCloneObjects(
                    sourceIds,
                    SymbolUtilityServices.GetBlockModelSpaceId(destinationDb),
                    mapping,
                    DuplicateRecordCloning.Ignore,
                    false);

                result.ImportedEntityCount = sourceIds.Count;
                result.DestinationHandles.AddRange(ReadClonedHandles(destinationDb, mapping));
                ImportCfbkCogoPoints(cogoPointsToImport, result);
            }
        }

        private static List<string> ReadClonedHandles(Database db, IdMapping mapping)
        {
            List<string> handles = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (IdPair pair in mapping)
                {
                    if (!pair.IsCloned || pair.Value.IsNull || pair.Value.IsErased)
                        continue;

                    Entity entity = tr.GetObject(pair.Value, OpenMode.ForRead, false) as Entity;
                    if (entity != null)
                        handles.Add(entity.Handle.ToString());
                }

                tr.Commit();
            }

            return handles;
        }

        private static int DeleteCfbkImportedObjects(Database db, CfbkImportRecord record)
        {
            int deleted = 0;
            if (record == null || record.DestinationHandles.Count == 0)
                return deleted;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (string handleText in record.DestinationHandles)
                {
                    ObjectId id = GetObjectIdFromHandle(db, handleText);
                    if (id.IsNull || id.IsErased)
                        continue;

                    Entity entity = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (entity == null || entity.IsErased)
                        continue;

                    entity.Erase();
                    deleted++;
                }

                tr.Commit();
            }

            return deleted;
        }

        private static ObjectId GetObjectIdFromHandle(Database db, string handleText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(handleText))
                    return ObjectId.Null;

                long handleValue = Convert.ToInt64(handleText, 16);
                return db.GetObjectId(false, new Handle(handleValue), 0);
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static void ImportCfbkCogoPoints(IEnumerable<CfbkCogoPointData> sourcePoints, CfbkImportedDrawing result)
        {
            List<CfbkCogoPointData> points = sourcePoints == null ? new List<CfbkCogoPointData>() : sourcePoints.ToList();
            if (points.Count == 0)
                return;

            object civilDocument = GetActiveCivilDocument();
            object cogoPoints = GetPropertyValue(civilDocument, "CogoPoints");
            if (cogoPoints == null)
            {
                result.CogoPointImportFailureCount += points.Count;
                result.SkippedEntityCount += points.Count;
                return;
            }

            foreach (CfbkCogoPointData point in points)
            {
                try
                {
                    ObjectId createdId = AddCfbkCogoPoint(cogoPoints, point);
                    if (createdId.IsNull)
                    {
                        result.CogoPointImportFailureCount++;
                        result.SkippedEntityCount++;
                        continue;
                    }

                    ApplyCfbkCogoPointProperties(createdId, point);
                    result.ImportedCogoPointCount++;
                    result.ImportedEntityCount++;
                    string handle = GetHandleText(createdId);
                    if (!string.IsNullOrWhiteSpace(handle))
                        result.DestinationHandles.Add(handle);
                }
                catch
                {
                    result.CogoPointImportFailureCount++;
                    result.SkippedEntityCount++;
                }
            }
        }

        private static ObjectId AddCfbkCogoPoint(object cogoPoints, CfbkCogoPointData point)
        {
            Type type = cogoPoints.GetType();
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, "Add", StringComparison.OrdinalIgnoreCase))
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                object[] args = BuildCfbkCogoAddArguments(parameters, point);
                if (args == null)
                    continue;

                try
                {
                    object value = method.Invoke(cogoPoints, args);
                    ObjectId id = ExtractObjectIdFromAddResult(value);
                    if (!id.IsNull)
                        return id;
                }
                catch
                {
                }
            }

            return ObjectId.Null;
        }

        private static object[] BuildCfbkCogoAddArguments(ParameterInfo[] parameters, CfbkCogoPointData point)
        {
            if (parameters.Length == 2 &&
                parameters[0].ParameterType == typeof(Point3d) &&
                parameters[1].ParameterType == typeof(bool))
            {
                return new object[] { point.Location, true };
            }

            if (parameters.Length == 5 &&
                parameters[0].ParameterType == typeof(Point3dCollection) &&
                parameters[1].ParameterType == typeof(string) &&
                parameters[2].ParameterType == typeof(bool) &&
                parameters[3].ParameterType == typeof(bool) &&
                parameters[4].ParameterType == typeof(bool))
            {
                Point3dCollection locations = new Point3dCollection { point.Location };
                return new object[] { locations, point.RawDescription ?? string.Empty, false, false, true };
            }

            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Point3d))
                return new object[] { point.Location };

            return null;
        }

        private static ObjectId ExtractObjectIdFromAddResult(object value)
        {
            if (value is ObjectId id)
                return id;

            ObjectIdCollection ids = value as ObjectIdCollection;
            if (ids != null && ids.Count > 0)
                return ids[0];

            System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null || value is string)
                return ObjectId.Null;

            try
            {
                foreach (object item in enumerable)
                {
                    if (item is ObjectId itemId)
                        return itemId;
                }
            }
            catch
            {
            }

            return ObjectId.Null;
        }

        private static void ApplyCfbkCogoPointProperties(ObjectId id, CfbkCogoPointData point)
        {
            if (id.IsNull)
                return;

            Database db = id.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForWrite, false);
                if (obj != null)
                {
                    if (point.PointNumber.HasValue)
                        TrySetLongProperty(obj, point.PointNumber.Value, "PointNumber", "Number");
                    if (!string.IsNullOrWhiteSpace(point.PointName))
                    {
                        if (!TrySetStringProperty(obj, "PointName", point.PointName))
                            TrySetStringProperty(obj, "Name", point.PointName);
                    }

                    if (!string.IsNullOrWhiteSpace(point.RawDescription))
                    {
                        if (!TrySetStringProperty(obj, "RawDescription", point.RawDescription))
                            TrySetStringProperty(obj, "Description", point.RawDescription);
                    }
                }

                tr.Commit();
            }
        }

        private static string GetHandleText(ObjectId id)
        {
            try
            {
                if (id.IsNull || id.IsErased)
                    return string.Empty;

                using (Transaction tr = id.Database.TransactionManager.StartTransaction())
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead, false);
                    string handle = obj?.Handle.ToString() ?? string.Empty;
                    tr.Commit();
                    return handle;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void AddCfbkSkippedObjectType(CfbkImportedDrawing result, string objectType)
        {
            string key = string.IsNullOrWhiteSpace(objectType) ? "Unknown object" : objectType.Trim();
            int count;
            result.SkippedObjectTypes.TryGetValue(key, out count);
            result.SkippedObjectTypes[key] = count + 1;
        }

        private static string ClassifyCfbkSkippedObject(Transaction tr, DBObject obj)
        {
            if (obj == null)
                return "Unreadable object";

            if (IsCogoPoint(obj))
                return "COGO point";

            if (IsSurveyNetwork(obj))
                return "Survey network object";

            Entity entity = obj as Entity;
            if (entity == null)
                return GetCfbkObjectTypeName(obj);

            BlockReference block = entity as BlockReference;
            if (block != null && IsXrefBlockReference(tr, block))
                return "Xref or overlay block reference";

            string typeName = GetCfbkObjectTypeName(entity);
            if (IsLikelyCfbkObjectType(typeName, "RASTER"))
                return "Raster image";
            if (IsLikelyCfbkObjectType(typeName, "UNDERLAY") || IsLikelyCfbkObjectType(typeName, "PDF") || IsLikelyCfbkObjectType(typeName, "DGN") || IsLikelyCfbkObjectType(typeName, "DWF"))
                return "Underlay";
            if (IsLikelyCfbkObjectType(typeName, "POINTCLOUD") || IsLikelyCfbkObjectType(typeName, "POINT CLOUD"))
                return "Point cloud";
            if (IsLikelyCfbkObjectType(typeName, "PROXY"))
                return "Proxy object";
            if (IsLikelyCfbkObjectType(typeName, "AECC"))
                return "Unsupported Civil object";

            return typeName;
        }

        private static bool IsLikelyCfbkObjectType(string typeName, string token)
        {
            return !string.IsNullOrWhiteSpace(typeName) &&
                   typeName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetCfbkObjectTypeName(DBObject obj)
        {
            try
            {
                RXClass rx = obj.GetRXClass();
                string dxfName = rx?.DxfName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(dxfName))
                    return dxfName;

                string rxName = rx?.Name ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(rxName))
                    return rxName;
            }
            catch
            {
            }

            return obj.GetType().Name ?? "Unknown object";
        }

        private static HashSet<string> CollectCfbkCogoKeys(Database db)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<ObjectId> civilPointIds = new List<ObjectId>();
                AddCivilDocumentCogoPointIds(civilPointIds);
                foreach (ObjectId id in civilPointIds)
                {
                    DBObject obj = GetDBObjectOrNull(tr, id);
                    if (IsCogoPoint(obj))
                        AddCfbkCogoKeys(keys, ReadCfbkCogoPointSignature(obj));
                }

                BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead, false);
                    if (IsCogoPoint(obj))
                        AddCfbkCogoKeys(keys, ReadCfbkCogoPointSignature(obj));
                }

                tr.Commit();
            }

            return keys;
        }

        private static bool HasCfbkCogoDuplicate(HashSet<string> existingKeys, CfbkCogoPointSignature signature)
        {
            foreach (string key in signature.Keys)
            {
                if (existingKeys.Contains(key))
                    return true;
            }

            return false;
        }

        private static void AddCfbkCogoKeys(HashSet<string> keys, CfbkCogoPointSignature signature)
        {
            foreach (string key in signature.Keys)
                keys.Add(key);
        }

        private static CfbkCogoPointSignature ReadCfbkCogoPointSignature(DBObject point)
        {
            CfbkCogoPointSignature signature = new CfbkCogoPointSignature();
            string pointNumber = ReadCfbkCogoString(point, "PointNumber", "Number");
            if (!string.IsNullOrWhiteSpace(pointNumber))
                signature.Keys.Add("N:" + pointNumber.Trim());

            string pointName = ReadCfbkCogoString(point, "PointName", "Name");
            if (!string.IsNullOrWhiteSpace(pointName))
                signature.Keys.Add("P:" + pointName.Trim());

            Point3d? location = ReadCfbkCogoLocation(point);
            if (location.HasValue)
            {
                Point3d value = location.Value;
                signature.Keys.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "XYZ:{0:0.###},{1:0.###},{2:0.###}",
                    value.X,
                    value.Y,
                    value.Z));
            }

            return signature;
        }

        private static CfbkCogoPointData ReadCfbkCogoPointData(DBObject point)
        {
            Point3d? location = ReadCfbkCogoLocation(point);
            if (!location.HasValue)
                return null;

            return new CfbkCogoPointData(
                location.Value,
                ReadCfbkCogoLong(point, "PointNumber", "Number"),
                ReadCfbkCogoString(point, "PointName", "Name"),
                ReadCfbkCogoString(point, "RawDescription", "Description", "FullDescription"));
        }

        private static string ReadCfbkCogoString(object point, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                object value = GetPropertyValue(point, propertyName);
                if (value == null)
                    continue;

                string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            return string.Empty;
        }

        private static Point3d? ReadCfbkCogoLocation(object point)
        {
            foreach (string propertyName in new[] { "Location", "Position" })
            {
                object value = GetPropertyValue(point, propertyName);
                if (value is Point3d point3d)
                    return point3d;
            }

            double? northing = ReadCfbkCogoDouble(point, "Northing", "Y");
            double? easting = ReadCfbkCogoDouble(point, "Easting", "X");
            double? elevation = ReadCfbkCogoDouble(point, "Elevation", "Z");
            if (northing.HasValue && easting.HasValue)
                return new Point3d(easting.Value, northing.Value, elevation ?? 0.0);

            return null;
        }

        private static double? ReadCfbkCogoDouble(object point, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                object value = GetPropertyValue(point, propertyName);
                if (value == null)
                    continue;

                try
                {
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch
                {
                }
            }

            return null;
        }

        private static long? ReadCfbkCogoLong(object point, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                object value = GetPropertyValue(point, propertyName);
                if (value == null)
                    continue;

                try
                {
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }
                catch
                {
                }
            }

            return null;
        }

        private static Dictionary<string, CfbkImportRecord> ReadCfbkImportRecords(Database db)
        {
            Dictionary<string, CfbkImportRecord> records = new Dictionary<string, CfbkImportRecord>(StringComparer.OrdinalIgnoreCase);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBDictionary dictionary = GetCfbkDictionary(db, tr, false);
                if (dictionary == null)
                {
                    tr.Commit();
                    return records;
                }

                foreach (DBDictionaryEntry entry in dictionary)
                {
                    Xrecord record = tr.GetObject(entry.Value, OpenMode.ForRead, false) as Xrecord;
                    CfbkImportRecord parsed = ParseCfbkImportRecord(record);
                    if (parsed != null && !string.IsNullOrWhiteSpace(parsed.NormalizedPath))
                        records[parsed.NormalizedPath] = parsed;
                }

                tr.Commit();
            }

            return records;
        }

        private static void RegisterCfbkImport(Database db, CfbkImportedDrawing drawing)
        {
            drawing.ImportedUtc = DateTime.UtcNow;
            string normalizedPath = NormalizeCfbkPath(drawing.SourcePath);
            string key = CreateCfbkImportKey(normalizedPath);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBDictionary dictionary = GetCfbkDictionary(db, tr, true);
                if (dictionary.Contains(key))
                {
                    Xrecord existing = tr.GetObject(dictionary.GetAt(key), OpenMode.ForWrite, false) as Xrecord;
                    if (existing != null)
                        existing.Data = CreateCfbkImportBuffer(drawing, normalizedPath);
                }
                else
                {
                    Xrecord record = new Xrecord { Data = CreateCfbkImportBuffer(drawing, normalizedPath) };
                    dictionary.SetAt(key, record);
                    tr.AddNewlyCreatedDBObject(record, true);
                }

                tr.Commit();
            }
        }

        private static DBDictionary GetCfbkDictionary(Database db, Transaction tr, bool create)
        {
            DBDictionary nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, create ? OpenMode.ForWrite : OpenMode.ForRead);
            if (nod.Contains(CfbkDictionaryName))
                return tr.GetObject(nod.GetAt(CfbkDictionaryName), create ? OpenMode.ForWrite : OpenMode.ForRead, false) as DBDictionary;

            if (!create)
                return null;

            DBDictionary dictionary = new DBDictionary();
            nod.SetAt(CfbkDictionaryName, dictionary);
            tr.AddNewlyCreatedDBObject(dictionary, true);
            return dictionary;
        }

        private static ResultBuffer CreateCfbkImportBuffer(CfbkImportedDrawing drawing, string normalizedPath)
        {
            List<TypedValue> values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "schema=" + CfbkImportSchema),
                new TypedValue((int)DxfCode.Text, "importedUtc=" + drawing.ImportedUtc.Value.ToString("o", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "sourceFile=" + Path.GetFileName(drawing.SourcePath)),
                new TypedValue((int)DxfCode.Text, "fileSizeBytes=" + drawing.FileSizeBytes.ToString(CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "lastWriteUtc=" + drawing.LastWriteUtc.ToString("o", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "objectsImported=" + drawing.ImportedEntityCount.ToString(CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "objectsSkipped=" + drawing.SkippedEntityCount.ToString(CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "cogoImported=" + drawing.ImportedCogoPointCount.ToString(CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "cogoDuplicates=" + drawing.DuplicateCogoPointCount.ToString(CultureInfo.InvariantCulture))
            };

            int index = 0;
            foreach (string chunk in ChunkCfbkText(normalizedPath, 220))
            {
                values.Add(new TypedValue((int)DxfCode.Text, "path" + index.ToString(CultureInfo.InvariantCulture) + "=" + chunk));
                index++;
            }

            foreach (string handle in drawing.DestinationHandles)
                values.Add(new TypedValue((int)DxfCode.Text, "handle=" + handle));

            return new ResultBuffer(values.ToArray());
        }

        private static CfbkImportRecord ParseCfbkImportRecord(Xrecord record)
        {
            if (record?.Data == null)
                return null;

            Dictionary<int, string> pathChunks = new Dictionary<int, string>();
            DateTime importedUtc = DateTime.MinValue;
            DateTime lastWriteUtc = DateTime.MinValue;
            long fileSizeBytes = -1;
            List<string> destinationHandles = new List<string>();
            bool hasSchema = false;
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (string.Equals(text, "schema=" + CfbkImportSchema, StringComparison.OrdinalIgnoreCase))
                {
                    hasSchema = true;
                    continue;
                }

                if (text.StartsWith("importedUtc=", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime parsed;
                    if (DateTime.TryParse(text.Substring("importedUtc=".Length), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
                        importedUtc = parsed.ToUniversalTime();
                    continue;
                }

                if (text.StartsWith("lastWriteUtc=", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime parsed;
                    if (DateTime.TryParse(text.Substring("lastWriteUtc=".Length), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
                        lastWriteUtc = parsed.ToUniversalTime();
                    continue;
                }

                if (text.StartsWith("fileSizeBytes=", StringComparison.OrdinalIgnoreCase))
                {
                    long parsed;
                    if (long.TryParse(text.Substring("fileSizeBytes=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        fileSizeBytes = parsed;
                    continue;
                }

                if (text.StartsWith("handle=", StringComparison.OrdinalIgnoreCase))
                {
                    string handle = text.Substring("handle=".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(handle))
                        destinationHandles.Add(handle);
                    continue;
                }

                Match match = Regex.Match(text, "^path([0-9]+)=(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success)
                    pathChunks[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)] = match.Groups[2].Value;
            }

            if (!hasSchema || pathChunks.Count == 0)
                return null;

            string normalizedPath = string.Concat(pathChunks.OrderBy(pair => pair.Key).Select(pair => pair.Value));
            return new CfbkImportRecord(normalizedPath, importedUtc, fileSizeBytes, lastWriteUtc, destinationHandles);
        }

        private static IEnumerable<string> ChunkCfbkText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                yield return string.Empty;
                yield break;
            }

            for (int offset = 0; offset < value.Length; offset += maxLength)
                yield return value.Substring(offset, Math.Min(maxLength, value.Length - offset));
        }

        private static string NormalizeCfbkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string CreateCfbkImportKey(string normalizedPath)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath.ToUpperInvariant()));
                StringBuilder builder = new StringBuilder("P_", 66);
                foreach (byte value in bytes)
                    builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static bool MatchesWildcard(string value, string pattern)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(pattern))
                return false;

            string regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static void InsertCfbkModelSpaceReport(Database db, Editor ed, CfbkRunResult result)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                double baseTextHeight = db.Textsize > 0.0 ? db.Textsize : 2.5;
                double textHeight = baseTextHeight * 20.0;
                double reportWidth = textHeight * 140.0;
                MText report = new MText
                {
                    Location = GetCfbkReportLocation(db, ed, result, reportWidth),
                    TextHeight = textHeight,
                    Width = reportWidth,
                    Attachment = AttachmentPoint.TopLeft,
                    Contents = BuildCfbkReportMText(result)
                };

                modelSpace.AppendEntity(report);
                tr.AddNewlyCreatedDBObject(report, true);
                tr.Commit();
            }
        }

        private static Point3d GetCfbkReportLocation(Database db, Editor ed, CfbkRunResult result, double reportWidth)
        {
            Extents3d? extents = GetCfbkImportedExtents(db, result);
            if (extents.HasValue)
                return new Point3d(extents.Value.MaxPoint.X + 500.0, extents.Value.MaxPoint.Y, 0.0);

            return GetCfbkFallbackReportLocation(ed);
        }

        private static Extents3d? GetCfbkImportedExtents(Database db, CfbkRunResult result)
        {
            Extents3d? combined = null;
            List<string> handles = result.Drawings
                .SelectMany(drawing => drawing.DestinationHandles)
                .Where(handle => !string.IsNullOrWhiteSpace(handle))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (handles.Count == 0)
                return null;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (string handle in handles)
                {
                    ObjectId id = GetObjectIdFromHandle(db, handle);
                    if (id.IsNull || id.IsErased)
                        continue;

                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity.IsErased)
                        continue;

                    try
                    {
                        Extents3d entityExtents = entity.GeometricExtents;
                        if (combined.HasValue)
                        {
                            Extents3d expanded = combined.Value;
                            expanded.AddExtents(entityExtents);
                            combined = expanded;
                        }
                        else
                        {
                            combined = entityExtents;
                        }
                    }
                    catch
                    {
                    }
                }

                tr.Commit();
            }

            return combined;
        }

        private static Point3d GetCfbkFallbackReportLocation(Editor ed)
        {
            try
            {
                ViewTableRecord view = ed.GetCurrentView();
                return new Point3d(
                    view.CenterPoint.X + (view.Width * 0.45),
                    view.CenterPoint.Y + (view.Height * 0.45),
                    0.0);
            }
            catch
            {
                return Point3d.Origin;
            }
        }

        private static string BuildCfbkReportMText(CfbkRunResult result)
        {
            List<string> lines = new List<string>
            {
                "CFBK IMPORT REPORT",
                "Run: " + result.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                "Source folder: " + result.SourceFolder,
                "Filter: " + result.Filter,
                "COGO points: " + (result.ImportCogoPoints ? "Import new points, skip duplicates" : "Skipped"),
                "Imported drawings: " + result.ImportedDrawingCount.ToString(CultureInfo.InvariantCulture),
                "Reimported changed drawings: " + result.ReimportedDrawingCount.ToString(CultureInfo.InvariantCulture),
                "Already imported and unchanged: " + result.AlreadyImportedCount.ToString(CultureInfo.InvariantCulture),
                "Legacy import records skipped: " + result.LegacyImportRecordCount.ToString(CultureInfo.InvariantCulture),
                "Import failures: " + result.ImportFailureCount.ToString(CultureInfo.InvariantCulture),
                "Ignored or filtered drawings: " + result.IgnoredDrawingCount.ToString(CultureInfo.InvariantCulture),
                "Previous objects erased: " + result.DeletedPreviousEntityCount.ToString(CultureInfo.InvariantCulture),
                "Objects imported: " + result.ImportedEntityCount.ToString(CultureInfo.InvariantCulture),
                "COGO points imported: " + result.ImportedCogoPointCount.ToString(CultureInfo.InvariantCulture),
                "Duplicate COGO points skipped: " + result.DuplicateCogoPointCount.ToString(CultureInfo.InvariantCulture),
                "COGO point import failures: " + result.CogoPointImportFailureCount.ToString(CultureInfo.InvariantCulture),
                string.Empty,
                "FILES"
            };

            foreach (CfbkImportedDrawing drawing in result.Drawings.OrderBy(drawing => drawing.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                string status = drawing.StatusText;
                string detail = string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} - {1}",
                    status,
                    drawing.SourcePath);

                if (drawing.ImportedEntityCount > 0 || drawing.SkippedEntityCount > 0)
                    detail += string.Format(CultureInfo.CurrentCulture, " ({0} imported, {1} skipped)", drawing.ImportedEntityCount, drawing.SkippedEntityCount);
                if (drawing.ImportedCogoPointCount > 0 || drawing.DuplicateCogoPointCount > 0)
                    detail += string.Format(CultureInfo.CurrentCulture, " ({0} COGO imported, {1} COGO duplicates)", drawing.ImportedCogoPointCount, drawing.DuplicateCogoPointCount);
                if (drawing.CogoPointImportFailureCount > 0)
                    detail += " (" + drawing.CogoPointImportFailureCount.ToString(CultureInfo.InvariantCulture) + " COGO failed)";
                if (drawing.DeletedPreviousEntityCount > 0)
                    detail += " (" + drawing.DeletedPreviousEntityCount.ToString(CultureInfo.InvariantCulture) + " previous erased)";
                if (drawing.LastWriteUtc != DateTime.MinValue)
                    detail += " (modified " + drawing.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) + ")";
                if (drawing.PreviousImportUtc.HasValue)
                    detail += " (previous import " + drawing.PreviousImportUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) + ")";
                if (!string.IsNullOrWhiteSpace(drawing.ImportError))
                    detail += " (error: " + drawing.ImportError + ")";

                lines.Add(detail);
            }

            return string.Join("\\P", lines.Select(EscapeMText));
        }

        private static string EscapeMText(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("{", "\\{")
                .Replace("}", "\\}");
        }

        private static bool IsAllowedCfbkEntity(Transaction tr, ObjectId id)
        {
            if (id.IsNull || id.IsErased)
                return false;

            Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
            if (entity == null)
                return false;

            if (IsCogoPoint(entity) || IsSurveyNetwork(entity))
                return false;

            if (entity is Line ||
                entity is Polyline ||
                entity is Polyline2d ||
                entity is Polyline3d ||
                entity is PolyFaceMesh ||
                entity is PolygonMesh ||
                entity is Arc ||
                entity is Circle ||
                entity is Spline ||
                entity is Ellipse ||
                entity is Hatch ||
                entity is Solid ||
                entity is Face ||
                entity is Trace ||
                entity is Region ||
                entity is Wipeout ||
                entity is Ray ||
                entity is Xline ||
                entity is DBPoint ||
                entity is DBText ||
                entity is MText ||
                entity is MLeader ||
                entity is Leader ||
                entity is Table ||
                entity is Dimension)
            {
                return true;
            }

            BlockReference blockReference = entity as BlockReference;
            if (blockReference == null)
                return false;

            return !IsXrefBlockReference(tr, blockReference);
        }

        private static bool IsXrefBlockReference(Transaction tr, BlockReference blockReference)
        {
            try
            {
                BlockTableRecord definition = tr.GetObject(blockReference.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                return definition == null || definition.IsFromExternalReference || definition.IsFromOverlayReference;
            }
            catch
            {
                return true;
            }
        }

        private static void WriteCfbkSummary(CfbkRunResult result)
        {
            string summaryPath = Path.Combine(result.LogFolder, "CFBK-summary.txt");
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("CFBK Summary");
            builder.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
            builder.AppendLine("Source folder: " + result.SourceFolder);
            builder.AppendLine("Filter: " + result.Filter);
            builder.AppendLine("COGO points: " + (result.ImportCogoPoints ? "Import new points, skip duplicates" : "Skipped"));
            builder.AppendLine("Log folder: " + result.LogFolder);
            builder.AppendLine("Source drawings: " + result.Drawings.Count.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Imported drawings: " + result.ImportedDrawingCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Reimported changed drawings: " + result.ReimportedDrawingCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Already imported and unchanged: " + result.AlreadyImportedCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Legacy import records skipped: " + result.LegacyImportRecordCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Import failures: " + result.ImportFailureCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Ignored or filtered drawings: " + result.IgnoredDrawingCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Previous objects erased: " + result.DeletedPreviousEntityCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Objects imported: " + result.ImportedEntityCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Objects skipped: " + result.SkippedEntityCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("COGO points imported: " + result.ImportedCogoPointCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Duplicate COGO points skipped: " + result.DuplicateCogoPointCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("COGO point import failures: " + result.CogoPointImportFailureCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine();

            foreach (CfbkImportedDrawing drawing in result.Drawings)
            {
                builder.AppendLine("Source: " + drawing.SourcePath);
                builder.AppendLine("  Status: " + drawing.StatusText);
                builder.AppendLine("  Objects imported: " + drawing.ImportedEntityCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("  Objects skipped: " + drawing.SkippedEntityCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("  COGO points imported: " + drawing.ImportedCogoPointCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("  Duplicate COGO points skipped: " + drawing.DuplicateCogoPointCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("  COGO point import failures: " + drawing.CogoPointImportFailureCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("  Previous objects erased: " + drawing.DeletedPreviousEntityCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("  Source file size: " + drawing.FileSizeBytes.ToString(CultureInfo.InvariantCulture));
                if (drawing.LastWriteUtc != DateTime.MinValue)
                    builder.AppendLine("  Source modified UTC: " + drawing.LastWriteUtc.ToString("o", CultureInfo.InvariantCulture));
                if (drawing.ImportedUtc.HasValue)
                    builder.AppendLine("  Imported UTC: " + drawing.ImportedUtc.Value.ToString("o", CultureInfo.InvariantCulture));
                if (drawing.PreviousImportUtc.HasValue)
                    builder.AppendLine("  Previous import UTC: " + drawing.PreviousImportUtc.Value.ToString("o", CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(drawing.SkipReason))
                    builder.AppendLine("  Skip reason: " + drawing.SkipReason);
                if (!string.IsNullOrWhiteSpace(drawing.ImportError))
                    builder.AppendLine("  Import error: " + drawing.ImportError);
                if (drawing.SkippedObjectTypes.Count > 0)
                {
                    builder.AppendLine("  Skipped object types:");
                    foreach (KeyValuePair<string, int> pair in drawing.SkippedObjectTypes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                        builder.AppendLine("    " + pair.Key + ": " + pair.Value.ToString(CultureInfo.InvariantCulture));
                }
            }

            File.WriteAllText(summaryPath, builder.ToString(), Encoding.UTF8);
        }

        private static void RunSelectionUtility(
            string actionName,
            string selectionMessage,
            Action<Database, Transaction, List<ObjectId>, FbkPrepResult, DraftingSuiteSettings> action,
            Action<Editor, FbkPrepResult> writeSummary)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc?.Editor;
            if (doc == null || ed == null)
                return;

            try
            {
                List<ObjectId> ids = PromptSelection(ed, selectionMessage);
                if (ids.Count == 0)
                {
                    ed.WriteMessage("\n{0} canceled or no objects selected.", actionName);
                    ed.WriteMessage("\n");
                    return;
                }

                DraftingSuiteSettings settings = DraftingSuiteSettings.Load();
                FbkPrepResult result = new FbkPrepResult();
                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    action(doc.Database, tr, ids, result, settings);
                    tr.Commit();
                }

                ed.WriteMessage("\n{0} complete.", actionName);
                writeSummary(ed, result);
                WriteWarnings(ed, result);
                ed.WriteMessage("\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n{0} failed: {1}", actionName, ex.Message);
                ed.WriteMessage("\n");
            }
        }

        private static void WriteWarnings(Editor ed, FbkPrepResult result)
        {
            if (result.Errors.Count == 0)
                return;

            ed.WriteMessage("\n  Warnings:");
            foreach (string error in result.Errors.Take(8))
                ed.WriteMessage("\n    {0}", error);
            if (result.Errors.Count > 8)
                ed.WriteMessage("\n    {0} more warning(s).", result.Errors.Count - 8);
        }

        private static FbkPrepScope PromptScope(Editor ed)
        {
            PromptKeywordOptions options = new PromptKeywordOptions("\nFBK Prep scope [Entire drawing/Selection]: ");
            options.Keywords.Add("Entire");
            options.Keywords.Add("Selection");
            options.Keywords.Default = "Entire";
            options.AllowNone = true;

            PromptResult result = ed.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return FbkPrepScope.Canceled;
            if (result.Status == PromptStatus.OK && string.Equals(result.StringResult, "Selection", StringComparison.OrdinalIgnoreCase))
                return FbkPrepScope.Selection;

            return FbkPrepScope.EntireDrawing;
        }

        private static FbkPrepResult RunFbkPrep(Database db, Editor ed, FbkPrepScope scope, DraftingSuiteSettings settings)
        {
            FbkPrepResult result = new FbkPrepResult();
            List<ObjectId> selectedIds = scope == FbkPrepScope.Selection ? PromptSelection(ed) : new List<ObjectId>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<ObjectId> candidateIds = scope == FbkPrepScope.Selection
                    ? selectedIds
                    : CollectModelSpaceEntityIds(db, tr);

                List<ObjectId> cogoIds = new List<ObjectId>();
                AddCivilDocumentCogoPointIds(cogoIds);
                foreach (ObjectId id in candidateIds)
                {
                    DBObject obj = GetDBObjectOrNull(tr, id);
                    if (!IsCogoPoint(obj))
                        continue;

                    if (!cogoIds.Contains(id))
                        cogoIds.Add(id);
                }
                result.CogoPointsFound = cogoIds.Count;

                List<ObjectId> createdIds = settings.ExtractCogoDisplayGraphics
                    ? ExplodeCogoDisplayGraphics(db, tr, cogoIds, result)
                    : new List<ObjectId>();

                if (settings.DeleteSurveyNetworks)
                    DeleteSurveyNetworks(tr, candidateIds, result);

                if (settings.ExplodeNamedBlocks)
                    ExplodeBlockReferences(db, tr, createdIds, result, settings.ExplodePassesBeforeBurst, "before burst");

                if (settings.BurstInserts)
                    BurstAnonymousBlocks(db, tr, createdIds, result, settings.MaxAnonymousBurstPasses);

                if (settings.ExplodeNamedBlocks)
                    ExplodeBlockReferences(db, tr, createdIds, result, settings.ExplodePassesAfterBurst, "after burst");

                if (settings.ConvertLinesTo3dPolylines)
                    ConvertLinesTo3dPolylines(tr, candidateIds.Concat(createdIds), result, settings);

                List<ObjectId> annotationIds = new List<ObjectId>(createdIds);
                annotationIds.AddRange(candidateIds.Where(id => IsEligibleSourceAnnotation(GetEntityOrNull(tr, id), settings)));

                DeleteTinyText(tr, annotationIds, result, settings);

                if (settings.ConvertTextToMleaders)
                    ConvertTextToMleaders(db, tr, annotationIds, result, settings, true, false, false);

                if (settings.FlattenAnnotation)
                {
                    List<ObjectId> flattenIds = CollectFlattenIds(candidateIds, createdIds, db, tr, settings);
                    FlattenObjects(tr, flattenIds, result, settings, false);
                }

                if (settings.RestyleCogoPoints)
                    RestyleCogoPoints(tr, cogoIds, result, settings);

                tr.Commit();
            }

            return result;
        }

        private static List<ObjectId> PromptSelection(Editor ed)
        {
            PromptSelectionResult result = ed.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect COGO points and annotation to prep: "
            });

            if (result.Status != PromptStatus.OK)
                return new List<ObjectId>();

            return result.Value.GetObjectIds().ToList();
        }

        private static List<ObjectId> PromptSelection(Editor ed, string message)
        {
            PromptSelectionResult result = ed.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message
            });

            if (result.Status != PromptStatus.OK)
                return new List<ObjectId>();

            return result.Value.GetObjectIds().ToList();
        }

        private static List<ObjectId> CollectModelSpaceEntityIds(Database db, Transaction tr)
        {
            List<ObjectId> ids = new List<ObjectId>();
            BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in modelSpace)
            {
                if (!id.IsErased)
                    ids.Add(id);
            }

            return ids;
        }

        private static List<ObjectId> ExplodeCogoDisplayGraphics(Database db, Transaction tr, List<ObjectId> cogoIds, FbkPrepResult result)
        {
            List<ObjectId> createdIds = new List<ObjectId>();
            BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            foreach (ObjectId id in cogoIds)
            {
                Entity source = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (source == null)
                    continue;

                try
                {
                    DBObjectCollection exploded = new DBObjectCollection();
                    source.Explode(exploded);
                    foreach (DBObject obj in exploded)
                    {
                        Entity entity = obj as Entity;
                        if (entity == null)
                        {
                            obj.Dispose();
                            continue;
                        }

                        ApplyInheritedLayer(entity, source.Layer);
                        ApplyByLayerProperties(entity, result);
                        ObjectId createdId = modelSpace.AppendEntity(entity);
                        tr.AddNewlyCreatedDBObject(entity, true);
                        createdIds.Add(createdId);
                        result.CogoDisplayObjectsCreated++;
                    }
                }
                catch (System.Exception ex)
                {
                    result.Errors.Add("COGO explode skipped " + id.Handle + ": " + ex.Message);
                }
            }

            return createdIds;
        }

        private static void BurstAnonymousBlocks(Database db, Transaction tr, List<ObjectId> candidateIds, FbkPrepResult result, int maxPasses)
        {
            if (maxPasses <= 0)
                return;

            BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            HashSet<ObjectId> knownIds = new HashSet<ObjectId>(candidateIds);

            for (int pass = 0; pass < maxPasses; pass++)
            {
                List<ObjectId> anonymousBlocks = candidateIds
                    .Where(id => IsAnonymousBlockReference(tr, id))
                    .ToList();

                if (anonymousBlocks.Count == 0)
                    return;

                int burstThisPass = 0;
                foreach (ObjectId id in anonymousBlocks)
                {
                    BlockReference block = tr.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                    if (block == null || block.IsErased)
                        continue;

                    try
                    {
                        int replacementCount = 0;
                        DBObjectCollection exploded = new DBObjectCollection();
                        block.Explode(exploded);
                        foreach (DBObject obj in exploded)
                        {
                            Entity entity = obj as Entity;
                            if (entity == null)
                            {
                                obj.Dispose();
                                continue;
                            }
                            if (entity is AttributeDefinition)
                            {
                                obj.Dispose();
                                continue;
                            }

                            ApplyInheritedLayer(entity, block.Layer);
                            ApplyByLayerProperties(entity, result);
                            ObjectId createdId = modelSpace.AppendEntity(entity);
                            tr.AddNewlyCreatedDBObject(entity, true);
                            if (knownIds.Add(createdId))
                                candidateIds.Add(createdId);
                            replacementCount++;
                        }

                        replacementCount += AppendAttributeText(db, tr, block, modelSpace, candidateIds, knownIds, result);
                        if (replacementCount == 0)
                        {
                            result.Errors.Add("Anonymous block burst created no replacement objects " + id.Handle + "; original block was kept.");
                            continue;
                        }

                        block.UpgradeOpen();
                        block.Erase();
                        result.AnonymousBlocksBurst++;
                        burstThisPass++;
                    }
                    catch (System.Exception ex)
                    {
                        result.Errors.Add("Anonymous block burst skipped " + id.Handle + ": " + ex.Message);
                    }
                }

                if (burstThisPass == 0)
                    return;
            }

            if (candidateIds.Any(id => IsAnonymousBlockReference(tr, id)))
                result.Errors.Add("Anonymous block burst stopped after max passes: " + maxPasses);
        }

        private static void ExplodeBlockReferences(Database db, Transaction tr, List<ObjectId> candidateIds, FbkPrepResult result, int passes, string phase)
        {
            if (passes <= 0)
                return;

            BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            HashSet<ObjectId> knownIds = new HashSet<ObjectId>(candidateIds);

            for (int pass = 0; pass < passes; pass++)
            {
                List<ObjectId> blocks = candidateIds
                    .Where(id => IsRegularBlockReference(tr, id))
                    .ToList();

                if (blocks.Count == 0)
                    return;

                int explodedThisPass = 0;
                foreach (ObjectId id in blocks)
                {
                    BlockReference block = tr.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                    if (block == null || block.IsErased)
                        continue;

                    try
                    {
                        int replacementCount = 0;
                        DBObjectCollection exploded = new DBObjectCollection();
                        block.Explode(exploded);
                        foreach (DBObject obj in exploded)
                        {
                            Entity entity = obj as Entity;
                            if (entity == null)
                            {
                                obj.Dispose();
                                continue;
                            }
                            if (entity is AttributeDefinition)
                            {
                                obj.Dispose();
                                continue;
                            }

                            ApplyInheritedLayer(entity, block.Layer);
                            ApplyByLayerProperties(entity, result);
                            ObjectId createdId = modelSpace.AppendEntity(entity);
                            tr.AddNewlyCreatedDBObject(entity, true);
                            if (knownIds.Add(createdId))
                                candidateIds.Add(createdId);
                            replacementCount++;
                        }

                        if (replacementCount == 0)
                        {
                            result.Errors.Add("Block explode " + phase + " created no replacement objects " + id.Handle + "; original block was kept.");
                            continue;
                        }

                        block.UpgradeOpen();
                        block.Erase();
                        result.BlockReferencesExploded++;
                        explodedThisPass++;
                    }
                    catch (System.Exception ex)
                    {
                        result.Errors.Add("Block explode " + phase + " skipped " + id.Handle + ": " + ex.Message);
                    }
                }

                if (explodedThisPass == 0)
                    return;
            }
        }

        private static void ConvertTextToMleaders(Database db, Transaction tr, List<ObjectId> annotationIds, FbkPrepResult result, DraftingSuiteSettings settings, bool applyLayerRules, bool useStyleBasedLanding, bool useNativeMLeaderStyle)
        {
            BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            HashSet<ObjectId> uniqueIds = new HashSet<ObjectId>(annotationIds);

            foreach (ObjectId id in uniqueIds.ToList())
            {
                Entity entity = GetEntityOrNull(tr, id);
                TextInfo text = ReadTextInfo(entity);
                if (text == null)
                    continue;
                bool forceConvertByLayer = applyLayerRules && ShouldForceConvertTextByLayer(text.Layer, settings);
                if (applyLayerRules && ShouldKeepTextByLayer(text.Layer, settings))
                {
                    ApplyByLayerPropertiesForExistingEntity(entity, result);
                    result.TextKeptByLayer++;
                    continue;
                }
                if (applyLayerRules && !forceConvertByLayer && MatchesAnyWildcard(text.Layer, settings.MLeaderDeleteLayerPatterns))
                {
                    DeleteTextByLayerRule(entity, id, result);
                    continue;
                }

                try
                {
                    entity.UpgradeOpen();
                    MLeader leader = CreateMLeader(db, tr, text, settings, result, useStyleBasedLanding, useNativeMLeaderStyle);
                    ObjectId leaderId = modelSpace.AppendEntity(leader);
                    tr.AddNewlyCreatedDBObject(leader, true);
                    entity.Erase();
                    result.TextConvertedToMleaders++;
                    annotationIds.Add(leaderId);
                }
                catch (System.Exception ex)
                {
                    result.Errors.Add("Text to mleader skipped " + id.Handle + ": " + ex.Message);
                }
            }
        }

        private static int AppendAttributeText(Database db, Transaction tr, BlockReference block, BlockTableRecord owner, List<ObjectId> candidateIds, HashSet<ObjectId> knownIds, FbkPrepResult result)
        {
            int created = 0;
            foreach (ObjectId attributeId in block.AttributeCollection)
            {
                AttributeReference attribute = tr.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                if (attribute == null || attribute.IsErased || attribute.Invisible || string.IsNullOrWhiteSpace(attribute.TextString))
                    continue;

                DBText text = new DBText();
                text.SetDatabaseDefaults(db);
                text.SetPropertiesFrom(attribute);
                text.TextString = attribute.TextString;
                text.Position = attribute.Position;
                text.Height = attribute.Height;
                text.Rotation = attribute.Rotation;
                text.Layer = attribute.Layer;
                text.TextStyleId = attribute.TextStyleId;
                text.WidthFactor = attribute.WidthFactor;
                text.Oblique = attribute.Oblique;
                text.HorizontalMode = attribute.HorizontalMode;
                text.VerticalMode = attribute.VerticalMode;
                ApplyByLayerProperties(text, result);
                try
                {
                    text.AlignmentPoint = attribute.AlignmentPoint;
                }
                catch
                {
                }

                ObjectId createdId = owner.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                if (knownIds.Add(createdId))
                    candidateIds.Add(createdId);
                created++;
            }

            return created;
        }

        private static void ApplyInheritedLayer(Entity entity, string parentLayer)
        {
            if (entity == null || string.IsNullOrWhiteSpace(parentLayer))
                return;

            if (string.Equals(entity.Layer, "0", StringComparison.OrdinalIgnoreCase))
                entity.Layer = parentLayer;
        }

        private static void DeleteTextByLayerRule(Entity entity, ObjectId id, FbkPrepResult result)
        {
            try
            {
                entity.UpgradeOpen();
                entity.Erase();
                result.TextDeletedByLayer++;
            }
            catch (System.Exception ex)
            {
                result.Errors.Add("Layer-ignored text delete skipped " + id.Handle + ": " + ex.Message);
            }
        }

        private static void DeleteTinyText(Transaction tr, IEnumerable<ObjectId> annotationIds, FbkPrepResult result, DraftingSuiteSettings settings)
        {
            double maxHeight = settings.TinyTextDeleteHeight;
            if (maxHeight <= 0.0)
                return;

            foreach (ObjectId id in new HashSet<ObjectId>(annotationIds))
            {
                Entity entity = GetEntityOrNull(tr, id);
                TextInfo text = ReadTextInfo(entity);
                if (text == null || text.Height >= maxHeight)
                    continue;
                try
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                    result.TinyTextDeleted++;
                }
                catch (System.Exception ex)
                {
                    result.Errors.Add("Small text delete skipped " + id.Handle + ": " + ex.Message);
                }
            }
        }

        private static MLeader CreateMLeader(Database db, Transaction tr, TextInfo text, DraftingSuiteSettings settings, FbkPrepResult result, bool useStyleBasedLanding, bool useNativeMLeaderStyle)
        {
            Point3d arrowPoint = ToTargetZ(text.Position, settings.FlattenElevation);
            Vector3d landingOffset = GetMLeaderLandingOffset(db, tr, text, settings, useStyleBasedLanding);
            Point3d textPoint = arrowPoint.Add(landingOffset);
            MText mText = new MText();
            mText.SetDatabaseDefaults(db);
            mText.Contents = text.Contents;
            mText.TextHeight = text.Height > 0.0 ? text.Height : db.Textsize;
            mText.Location = textPoint;
            mText.Rotation = text.Rotation;
            mText.Attachment = AttachmentPoint.MiddleLeft;

            MLeader leader = new MLeader();
            leader.SetDatabaseDefaults(db);
            ObjectId styleId = GetCurrentMLeaderStyleId(db);
            if (useNativeMLeaderStyle)
            {
                TrySetObjectIdProperty(leader, styleId, "MLeaderStyle", "MLeaderStyleId");
                CopyMLeaderStyleProperties(tr, leader, styleId);
            }
            leader.Layer = text.Layer;
            leader.ContentType = ContentType.MTextContent;
            int leaderIndex = leader.AddLeader();
            int lineIndex = leader.AddLeaderLine(leaderIndex);
            leader.AddFirstVertex(lineIndex, arrowPoint);
            leader.AddLastVertex(lineIndex, textPoint);
            leader.MText = mText;
            TrySetPoint3dProperty(leader, textPoint, "TextLocation");
            TrySetStringProperty(leader, "TextString", text.Contents);
            if (useNativeMLeaderStyle)
            {
                TrySetDoglegDirection(leader, lineIndex, textPoint.X < arrowPoint.X ? new Vector3d(-1.0, 0.0, 0.0) : new Vector3d(1.0, 0.0, 0.0));
                CopyMLeaderStyleProperties(tr, leader, styleId);
            }
            TryInvokeParameterless(leader, "EvaluateLeader");
            ApplyByLayerProperties(leader, result);
            return leader;
        }

        private static void ApplyByLayerProperties(Entity entity, FbkPrepResult result)
        {
            if (entity == null)
                return;

            bool changed = false;
            try
            {
                if (entity.Color == null || entity.Color.ColorMethod != Autodesk.AutoCAD.Colors.ColorMethod.ByLayer)
                {
                    entity.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByLayer, 256);
                    changed = true;
                }
            }
            catch
            {
            }

            try
            {
                if (!string.Equals(entity.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase))
                {
                    entity.Linetype = "ByLayer";
                    changed = true;
                }
            }
            catch
            {
            }

            try
            {
                if (entity.LineWeight != LineWeight.ByLayer)
                {
                    entity.LineWeight = LineWeight.ByLayer;
                    changed = true;
                }
            }
            catch
            {
            }

            if (changed && result != null)
                result.ObjectsSetByLayer++;
        }

        private static void ApplyByLayerPropertiesForExistingEntity(Entity entity, FbkPrepResult result)
        {
            try
            {
                entity.UpgradeOpen();
                ApplyByLayerProperties(entity, result);
            }
            catch (System.Exception ex)
            {
                result.Errors.Add("ByLayer cleanup skipped " + entity.ObjectId.Handle + ": " + ex.Message);
            }
        }

        private static void ApplyByLayerToEntities(Transaction tr, IEnumerable<ObjectId> ids, FbkPrepResult result)
        {
            foreach (ObjectId id in ids.ToList())
            {
                Entity entity = GetEntityOrNull(tr, id);
                if (entity == null || entity.IsErased)
                    continue;

                ApplyByLayerPropertiesForExistingEntity(entity, result);
            }
        }

        private static bool ShouldKeepTextByLayer(string layerName, DraftingSuiteSettings settings)
        {
            bool hasKeepRules = HasWildcardRules(settings.MLeaderKeepTextLayerPatterns);
            if (!hasKeepRules)
                return false;

            bool matchesKeepRule = MatchesAnyWildcard(layerName, settings.MLeaderKeepTextLayerPatterns);
            return settings.InvertKeepTextLayerPatterns ? !matchesKeepRule : matchesKeepRule;
        }

        private static bool ShouldForceConvertTextByLayer(string layerName, DraftingSuiteSettings settings)
        {
            if (!settings.InvertKeepTextLayerPatterns || !HasWildcardRules(settings.MLeaderKeepTextLayerPatterns))
                return false;

            return MatchesAnyWildcard(layerName, settings.MLeaderKeepTextLayerPatterns);
        }

        private static List<ObjectId> CollectFlattenIds(List<ObjectId> candidateIds, List<ObjectId> createdIds, Database db, Transaction tr, DraftingSuiteSettings settings)
        {
            HashSet<ObjectId> ids = new HashSet<ObjectId>(createdIds);
            foreach (ObjectId id in candidateIds)
            {
                Entity entity = GetEntityOrNull(tr, id);
                if (IsEligibleSourceAnnotation(entity, settings))
                    ids.Add(id);
            }

            return ids.ToList();
        }

        private static void DeleteSurveyNetworks(Transaction tr, IEnumerable<ObjectId> ids, FbkPrepResult result)
        {
            foreach (ObjectId id in ids.ToList())
            {
                DBObject obj = GetDBObjectOrNull(tr, id);
                if (!IsSurveyNetwork(obj))
                    continue;

                try
                {
                    obj = tr.GetObject(id, OpenMode.ForWrite, false);
                    obj.Erase();
                    result.SurveyNetworksDeleted++;
                }
                catch (System.Exception ex)
                {
                    result.Errors.Add("Survey network delete skipped " + id.Handle + ": " + ex.Message);
                }
            }
        }

        private static void ConvertLinesTo3dPolylines(Transaction tr, IEnumerable<ObjectId> ids, FbkPrepResult result, DraftingSuiteSettings settings)
        {
            foreach (ObjectId id in ids.ToList())
            {
                Line line = GetEntityOrNull(tr, id) as Line;
                if (line == null || line.IsErased)
                    continue;

                try
                {
                    if (MatchesAnyWildcard(line.Layer, settings.ProtectedSourceLayerPatterns))
                    {
                        line.UpgradeOpen();
                        line.Erase();
                        result.CogoLayerLinesDeleted++;
                        continue;
                    }

                    BlockTableRecord owner = tr.GetObject(line.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                    if (owner == null)
                        continue;

                    Point3dCollection vertices = new Point3dCollection
                    {
                        line.StartPoint,
                        line.EndPoint
                    };
                    Polyline3d polyline = new Polyline3d(Poly3dType.SimplePoly, vertices, false);
                    polyline.SetPropertiesFrom(line);
                    ApplyByLayerProperties(polyline, result);

                    owner.AppendEntity(polyline);
                    tr.AddNewlyCreatedDBObject(polyline, true);

                    line.UpgradeOpen();
                    line.Erase();
                    result.LinesConvertedTo3dPolylines++;
                }
                catch (System.Exception ex)
                {
                    result.Errors.Add("Line to 3D polyline skipped " + id.Handle + ": " + ex.Message);
                }
            }
        }

        private static void AddCivilDocumentCogoPointIds(List<ObjectId> cogoIds)
        {
            object civilDocument = GetActiveCivilDocument();
            object cogoPoints = GetPropertyValue(civilDocument, "CogoPoints");
            AddObjectIdsFromValue(cogoIds, cogoPoints);

            object countValue = GetPropertyValue(cogoPoints, "Count");
            if (!(countValue is int count) || count <= 0)
                return;

            foreach (PropertyInfo property in cogoPoints.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                ParameterInfo[] indexParameters = property.GetIndexParameters();
                if (indexParameters.Length != 1 || indexParameters[0].ParameterType != typeof(int))
                    continue;

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        AddObjectIdsFromValue(cogoIds, property.GetValue(cogoPoints, new object[] { i }));
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void AddObjectIdsFromValue(List<ObjectId> objectIds, object value)
        {
            if (value == null)
                return;

            if (value is ObjectId id)
            {
                if (!id.IsNull && !objectIds.Contains(id))
                    objectIds.Add(id);
                return;
            }

            if (value is DBObject dbObject)
            {
                ObjectId objectId = dbObject.ObjectId;
                if (!objectId.IsNull && !objectIds.Contains(objectId))
                    objectIds.Add(objectId);
                return;
            }

            if (value is string)
                return;

            System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null)
                return;

            try
            {
                foreach (object item in enumerable)
                    AddObjectIdsFromValue(objectIds, item);
            }
            catch
            {
            }
        }

        private static void FlattenObjects(Transaction tr, IEnumerable<ObjectId> ids, FbkPrepResult result, DraftingSuiteSettings settings, bool broadFlatten)
        {
            foreach (ObjectId id in ids)
            {
                Entity entity = GetEntityOrNull(tr, id);
                if (entity == null || entity.IsErased)
                    continue;

                try
                {
                    if (ShouldSkipFlatten(tr, entity, settings.FlattenSkipBlockNamePatterns))
                    {
                        result.BlocksSkippedByFlattenRule++;
                        continue;
                    }

                    if (FlattenEntity(tr, entity, settings.FlattenElevation, broadFlatten))
                    {
                        if (entity is BlockReference)
                            result.BlocksFlattened++;
                        else
                            result.AnnotationObjectsFlattened++;
                        ApplyByLayerProperties(entity, result);
                    }
                }
                catch (System.Exception ex)
                {
                    result.Errors.Add("Flatten skipped " + id.Handle + ": " + ex.Message);
                }
            }
        }

        private static bool ShouldSkipFlatten(Transaction tr, Entity entity, IEnumerable<string> blockNamePatterns)
        {
            BlockReference block = entity as BlockReference;
            if (block == null)
                return false;

            return MatchesAnyWildcard(GetBlockReferenceName(tr, block), blockNamePatterns);
        }

        private static string GetBlockReferenceName(Transaction tr, BlockReference block)
        {
            string dynamicName = GetBlockTableRecordName(tr, GetObjectIdPropertyValue(block, "DynamicBlockTableRecord"));
            if (!string.IsNullOrWhiteSpace(dynamicName) && !dynamicName.StartsWith("*", StringComparison.Ordinal))
                return dynamicName;

            string name = GetBlockTableRecordName(tr, block.BlockTableRecord);
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            object reflectedName = GetPropertyValue(block, "Name");
            return Convert.ToString(reflectedName) ?? string.Empty;
        }

        private static string GetBlockTableRecordName(Transaction tr, ObjectId id)
        {
            try
            {
                if (id.IsNull || id.IsErased)
                    return string.Empty;

                BlockTableRecord record = tr.GetObject(id, OpenMode.ForRead, false) as BlockTableRecord;
                return record?.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool FlattenEntity(Transaction tr, Entity entity, double targetElevation, bool broadFlatten)
        {
            if (!broadFlatten && !IsDraftingAnnotation(entity))
                return false;

            entity.UpgradeOpen();
            if (entity is BlockReference block)
            {
                block.Position = ToTargetZ(block.Position, targetElevation);
                return true;
            }
            if (entity is DBText dbText)
            {
                dbText.Position = ToTargetZ(dbText.Position, targetElevation);
                if (!dbText.AlignmentPoint.IsEqualTo(Point3d.Origin))
                    dbText.AlignmentPoint = ToTargetZ(dbText.AlignmentPoint, targetElevation);
                return true;
            }
            if (entity is MText mText)
            {
                mText.Location = ToTargetZ(mText.Location, targetElevation);
                return true;
            }
            if (entity is MLeader mLeader)
            {
                FlattenByExtents(mLeader, targetElevation);
                return true;
            }
            if (entity is Leader leader)
            {
                FlattenByExtents(leader, targetElevation);
                return true;
            }
            if (entity is Dimension dimension)
            {
                FlattenByExtents(dimension, targetElevation);
                return true;
            }
            if (entity is Line line)
            {
                line.StartPoint = ToTargetZ(line.StartPoint, targetElevation);
                line.EndPoint = ToTargetZ(line.EndPoint, targetElevation);
                return true;
            }
            if (entity is Polyline polyline)
            {
                polyline.Elevation = targetElevation;
                return true;
            }
            if (entity is Polyline2d polyline2d)
            {
                polyline2d.Elevation = targetElevation;
                return true;
            }
            if (entity is Polyline3d polyline3d)
            {
                FlattenPolyline3d(tr, polyline3d, targetElevation);
                return true;
            }
            if (entity is Circle circle)
            {
                circle.Center = ToTargetZ(circle.Center, targetElevation);
                return true;
            }
            if (entity is Arc arc)
            {
                arc.Center = ToTargetZ(arc.Center, targetElevation);
                return true;
            }
            if (entity is DBPoint point)
            {
                point.Position = ToTargetZ(point.Position, targetElevation);
                return true;
            }
            if (entity is Solid solid)
            {
                solid.SetPointAt(0, ToTargetZ(solid.GetPointAt(0), targetElevation));
                solid.SetPointAt(1, ToTargetZ(solid.GetPointAt(1), targetElevation));
                solid.SetPointAt(2, ToTargetZ(solid.GetPointAt(2), targetElevation));
                solid.SetPointAt(3, ToTargetZ(solid.GetPointAt(3), targetElevation));
                return true;
            }

            FlattenByExtents(entity, targetElevation);
            return true;
        }

        private static Vector3d GetMLeaderLandingOffset(Database db, Transaction tr, TextInfo text, DraftingSuiteSettings settings, bool useStyleBasedLanding)
        {
            if (!useStyleBasedLanding)
                return new Vector3d(settings.MLeaderTextOffsetX, settings.MLeaderTextOffsetY, 0.0);

            double textHeight = GetCurrentMLeaderStyleTextHeight(db, tr);
            if (textHeight <= 0.0)
                textHeight = text.Height;
            if (textHeight <= 0.0)
                textHeight = db.Textsize;
            if (textHeight <= 0.0)
                textHeight = 1.0;

            double distance = 15.0;
            return new Vector3d(distance, distance, 0.0);
        }

        private static double GetCurrentMLeaderStyleTextHeight(Database db, Transaction tr)
        {
            ObjectId styleId = GetCurrentMLeaderStyleId(db);
            if (styleId.IsNull || tr == null)
                return 0.0;

            try
            {
                DBObject style = tr.GetObject(styleId, OpenMode.ForRead, false);
                object value = GetPropertyValue(style, "TextHeight");
                return Convert.ToDouble(value);
            }
            catch
            {
                return 0.0;
            }
        }

        private static void CopyMLeaderStyleProperties(Transaction tr, MLeader leader, ObjectId styleId)
        {
            if (tr == null || leader == null || styleId.IsNull)
                return;

            try
            {
                DBObject style = tr.GetObject(styleId, OpenMode.ForRead, false);
                CopyBooleanProperty(style, leader, "EnableLanding");
                CopyBooleanProperty(style, leader, "EnableDogleg");
                CopyBooleanProperty(style, leader, "EnableFrameText");
                CopyDoubleProperty(style, leader, "DoglegLength", "DoglegLength", "LandingDistance");
                CopyDoubleProperty(style, leader, "LandingGap", "LandingGap", "TextLandingGap");
                CopyDoubleProperty(style, leader, "TextHeight", "TextHeight");
                CopyEnumProperty(style, leader, "TextAlignmentType");
                CopyEnumProperty(style, leader, "TextAttachmentType");
            }
            catch
            {
            }
        }

        private static void CopyBooleanProperty(object source, object target, string propertyName)
        {
            object value = GetPropertyValue(source, propertyName);
            if (value is bool boolValue)
                TrySetBooleanProperty(target, boolValue, propertyName);
        }

        private static void CopyDoubleProperty(object source, object target, string sourcePropertyName, params string[] targetPropertyNames)
        {
            object value = GetPropertyValue(source, sourcePropertyName);
            try
            {
                if (value != null)
                    TrySetDoubleProperty(target, Convert.ToDouble(value), targetPropertyNames);
            }
            catch
            {
            }
        }

        private static void CopyEnumProperty(object source, object target, string propertyName)
        {
            object value = GetPropertyValue(source, propertyName);
            if (value != null)
                TrySetPropertyValue(target, propertyName, value);
        }

        private static void FlattenPolyline3d(Transaction tr, Polyline3d polyline, double targetElevation)
        {
            foreach (ObjectId vertexId in polyline)
            {
                try
                {
                    PolylineVertex3d vertex = tr.GetObject(vertexId, OpenMode.ForWrite, false) as PolylineVertex3d;
                    if (vertex != null)
                        vertex.Position = ToTargetZ(vertex.Position, targetElevation);
                }
                catch
                {
                }
            }
        }

        private static void FlattenByExtents(Entity entity, double targetElevation)
        {
            try
            {
                Extents3d extents = entity.GeometricExtents;
                double z = Math.Abs(extents.MinPoint.Z) <= Math.Abs(extents.MaxPoint.Z) ? extents.MinPoint.Z : extents.MaxPoint.Z;
                double delta = targetElevation - z;
                if (Math.Abs(delta) > 1e-8)
                    entity.TransformBy(Matrix3d.Displacement(new Vector3d(0.0, 0.0, delta)));
            }
            catch
            {
            }
        }

        private static void RestyleCogoPoints(Transaction tr, IEnumerable<ObjectId> cogoIds, FbkPrepResult result, DraftingSuiteSettings settings)
        {
            List<ObjectId> ids = cogoIds == null ? new List<ObjectId>() : cogoIds.ToList();
            ObjectId pointStyleId = FindCivilStyleId(tr, settings.CogoPointStyleName, "PointStyle");
            ObjectId labelStyleId = FindCivilStyleId(tr, settings.CogoLabelStyleName, "LabelStyle");
            if (ids.Count > 0 && pointStyleId.IsNull)
                result.Errors.Add("COGO point style not found: " + settings.CogoPointStyleName);
            if (ids.Count > 0 && labelStyleId.IsNull)
                result.Errors.Add("COGO label style not found: " + settings.CogoLabelStyleName);

            foreach (ObjectId id in ids)
            {
                DBObject obj = GetDBObjectOrNull(tr, id);
                if (!IsCogoPoint(obj))
                    continue;

                try
                {
                    obj = tr.GetObject(id, OpenMode.ForWrite, false);
                    bool pointStyleSet = false;
                    bool labelStyleSet = false;
                    if (!pointStyleId.IsNull)
                    {
                        pointStyleSet = TrySetObjectIdProperty(obj, pointStyleId, "StyleId", "PointStyleId");
                    }

                    if (!labelStyleId.IsNull)
                    {
                        labelStyleSet = TrySetObjectIdProperty(obj, labelStyleId, "LabelStyleId", "PointLabelStyleId");
                    }

                    if (pointStyleSet || labelStyleSet)
                        result.CogoPointsRestyled++;
                    else
                        result.CogoPointStyleSkipped++;
                }
                catch (System.Exception ex)
                {
                    result.CogoPointStyleSkipped++;
                    result.Errors.Add("COGO style skipped " + id.Handle + ": " + ex.Message);
                }
            }
        }

        private static ObjectId FindCivilStyleId(Transaction tr, string styleName, string styleKind)
        {
            if (string.IsNullOrWhiteSpace(styleName))
                return ObjectId.Null;

            object civilDocument = GetActiveCivilDocument();
            if (civilDocument == null)
                return ObjectId.Null;

            object stylesRoot = GetPropertyValue(civilDocument, "Styles");
            if (stylesRoot == null)
                return ObjectId.Null;

            ObjectId exactPathMatch = styleKind == "PointStyle"
                ? FindStyleIdByPath(tr, civilDocument, styleName.Trim(), styleKind, "Styles", "PointStyles")
                : FirstNonNullObjectId(
                    FindStyleIdByPath(tr, civilDocument, styleName.Trim(), styleKind, "Styles", "LabelStyles", "PointLabelStyles", "LabelStyles"),
                    FindStyleIdByPath(tr, civilDocument, styleName.Trim(), styleKind, "Styles", "LabelStyles", "PointLabelStyles"));
            if (!exactPathMatch.IsNull)
                return exactPathMatch;

            HashSet<object> visited = new HashSet<object>();
            return FindStyleIdRecursive(tr, stylesRoot, styleName.Trim(), styleKind, visited, 0);
        }

        private static object GetActiveCivilDocument()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType("Autodesk.Civil.ApplicationServices.CivilApplication", false);
                if (type == null)
                    continue;

                PropertyInfo property = type.GetProperty("ActiveDocument", BindingFlags.Public | BindingFlags.Static);
                if (property != null)
                    return property.GetValue(null, null);
            }

            return null;
        }

        private static ObjectId FirstNonNullObjectId(params ObjectId[] ids)
        {
            foreach (ObjectId id in ids)
            {
                if (!id.IsNull)
                    return id;
            }

            return ObjectId.Null;
        }

        private static ObjectId FindStyleIdByPath(Transaction tr, object root, string styleName, string styleKind, params string[] propertyPath)
        {
            object current = root;
            foreach (string propertyName in propertyPath)
            {
                current = GetPropertyValue(current, propertyName);
                if (current == null)
                    return ObjectId.Null;
            }

            ObjectId namedMatch = FindStyleIdByNameIndexer(tr, current, styleName, styleKind, true);
            if (!namedMatch.IsNull)
                return namedMatch;

            return FindStyleIdInEnumerable(tr, current, styleName, styleKind, true);
        }

        private static ObjectId FindStyleIdRecursive(Transaction tr, object value, string styleName, string styleKind, HashSet<object> visited, int depth)
        {
            if (value == null || depth > 8 || visited.Contains(value))
                return ObjectId.Null;

            visited.Add(value);

            if (value is ObjectId id)
            {
                if (TryStyleMatches(tr, id, styleName, styleKind))
                    return id;

                return ObjectId.Null;
            }

            ObjectId namedMatch = FindStyleIdByNameIndexer(tr, value, styleName, styleKind, false);
            if (!namedMatch.IsNull)
                return namedMatch;

            ObjectId enumerableMatch = FindStyleIdInEnumerable(tr, value, styleName, styleKind, false);
            if (!enumerableMatch.IsNull)
                return enumerableMatch;

            foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0)
                    continue;

                string propertyName = property.Name ?? string.Empty;
                bool likelyStylePath = propertyName.IndexOf("Style", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       propertyName.IndexOf("Label", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       propertyName.IndexOf("Point", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!likelyStylePath)
                    continue;

                object child;
                try
                {
                    child = property.GetValue(value, null);
                }
                catch
                {
                    continue;
                }

                ObjectId match = FindStyleIdRecursive(tr, child, styleName, styleKind, visited, depth + 1);
                if (!match.IsNull)
                    return match;
            }

            return ObjectId.Null;
        }

        private static ObjectId FindStyleIdByNameIndexer(Transaction tr, object value, string styleName, string styleKind, bool trustCollection)
        {
            if (value == null)
                return ObjectId.Null;

            Type type = value.GetType();
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                ParameterInfo[] indexParameters = property.GetIndexParameters();
                if (indexParameters.Length != 1 ||
                    (indexParameters[0].ParameterType != typeof(string) && indexParameters[0].ParameterType != typeof(object)))
                    continue;

                try
                {
                    object item = property.GetValue(value, new object[] { styleName });
                    ObjectId id = GetStyleObjectId(tr, item, styleName, styleKind, trustCollection);
                    if (!id.IsNull)
                        return id;
                }
                catch
                {
                }
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1 ||
                    (parameters[0].ParameterType != typeof(string) && parameters[0].ParameterType != typeof(object)))
                    continue;

                string methodName = method.Name ?? string.Empty;
                if (!string.Equals(methodName, "Item", StringComparison.OrdinalIgnoreCase) &&
                    methodName.IndexOf("Get", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    object item = method.Invoke(value, new object[] { styleName });
                    ObjectId id = GetStyleObjectId(tr, item, styleName, styleKind, trustCollection);
                    if (!id.IsNull)
                        return id;
                }
                catch
                {
                }
            }

            return ObjectId.Null;
        }

        private static ObjectId FindStyleIdInEnumerable(Transaction tr, object value, string styleName, string styleKind, bool trustCollection)
        {
            System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null || value is string)
                return ObjectId.Null;

            try
            {
                foreach (object item in enumerable)
                {
                    ObjectId id = GetStyleObjectId(tr, item, styleName, styleKind, trustCollection);
                    if (!id.IsNull)
                        return id;

                    if (item is string name && string.Equals(name, styleName, StringComparison.OrdinalIgnoreCase))
                    {
                        id = FindStyleIdByNameIndexer(tr, value, styleName, styleKind, true);
                        if (!id.IsNull)
                            return id;
                    }
                }
            }
            catch
            {
            }

            return ObjectId.Null;
        }

        private static ObjectId GetStyleObjectId(Transaction tr, object value, string styleName, string styleKind, bool trustCollection)
        {
            if (value == null)
                return ObjectId.Null;

            if (value is ObjectId id)
                return trustCollection || TryStyleMatches(tr, id, styleName, styleKind) ? id : ObjectId.Null;

            if (value is DBObject dbObject)
            {
                id = dbObject.ObjectId;
                return trustCollection || TryStyleMatches(tr, id, styleName, styleKind) ? id : ObjectId.Null;
            }

            id = GetObjectIdPropertyValue(value, "ObjectId");
            if (!id.IsNull && (trustCollection || TryStyleMatches(tr, id, styleName, styleKind)))
                return id;

            id = GetObjectIdPropertyValue(value, "Id");
            if (!id.IsNull && (trustCollection || TryStyleMatches(tr, id, styleName, styleKind)))
                return id;

            object name = GetPropertyValue(value, "Name");
            if (!string.Equals(Convert.ToString(name), styleName, StringComparison.OrdinalIgnoreCase))
                return ObjectId.Null;

            id = GetObjectIdPropertyValue(value, "StyleId");
            if (!id.IsNull)
                return id;

            return ObjectId.Null;
        }

        private static bool TryStyleMatches(Transaction tr, ObjectId id, string styleName, string styleKind)
        {
            if (id.IsNull || id.IsErased)
                return false;

            try
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead, false);
                if (obj == null)
                    return false;

                string typeName = obj.GetType().FullName ?? obj.GetType().Name;
                if (styleKind == "PointStyle" && typeName.IndexOf("PointStyle", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
                if (styleKind == "LabelStyle" && typeName.IndexOf("LabelStyle", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

                object name = GetPropertyValue(obj, "Name");
                return string.Equals(Convert.ToString(name), styleName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static object GetPropertyValue(object target, string propertyName)
        {
            if (target == null)
                return null;

            try
            {
                PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property != null)
                    return property.GetValue(target, null);

                FieldInfo field = target.GetType().GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
                return field == null ? null : field.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static ObjectId GetObjectIdPropertyValue(object target, string propertyName)
        {
            object value = GetPropertyValue(target, propertyName);
            return value is ObjectId id ? id : ObjectId.Null;
        }

        private static ObjectId GetObjectIdPropertyValue(object target, params string[] propertyNames)
        {
            if (propertyNames == null)
                return ObjectId.Null;

            foreach (string propertyName in propertyNames)
            {
                ObjectId value = GetObjectIdPropertyValue(target, propertyName);
                if (!value.IsNull)
                    return value;
            }

            return ObjectId.Null;
        }

        private static ObjectId GetCurrentMLeaderStyleId(Database db)
        {
            return GetObjectIdPropertyValue(
                db,
                "Cmlstyle",
                "CMLeaderStyle",
                "CurrentMLeaderStyle",
                "CurrentMLeaderStyleId",
                "MLeaderStyle",
                "MLeaderStyleId");
        }

        private static bool IsDraftingAnnotation(Entity entity)
        {
            if (entity == null || IsCogoPoint(entity))
                return false;

            return entity is BlockReference ||
                   entity is DBText ||
                   entity is MText ||
                   entity is MLeader ||
                   entity is Leader ||
                   entity is Dimension;
        }

        private static bool IsEligibleSourceAnnotation(Entity entity, DraftingSuiteSettings settings)
        {
            if (!IsDraftingAnnotation(entity))
                return false;

            return !HasWildcardRules(settings.AnnotationLayerPatterns)
                || MatchesAnyWildcard(entity.Layer, settings.AnnotationLayerPatterns);
        }

        private static bool IsAnonymousBlockReference(Transaction tr, ObjectId id)
        {
            try
            {
                if (id.IsErased)
                    return false;

                BlockReference block = tr.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                if (block == null || block.IsErased || block.BlockTableRecord.IsNull)
                    return false;

                if (HasNamedDynamicDefinition(tr, block))
                    return false;

                BlockTableRecord definition = tr.GetObject(block.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                if (definition == null || definition.IsFromExternalReference || definition.IsLayout)
                    return false;

                string blockName = GetBlockReferenceName(tr, block);
                if (!string.IsNullOrWhiteSpace(blockName) && !blockName.StartsWith("*", StringComparison.Ordinal))
                    return false;

                return definition.IsAnonymous ||
                       (!string.IsNullOrWhiteSpace(definition.Name) && definition.Name.StartsWith("*", StringComparison.Ordinal));
            }
            catch
            {
                return false;
            }
        }

        private static bool HasNamedDynamicDefinition(Transaction tr, BlockReference block)
        {
            try
            {
                ObjectId dynamicDefinitionId = GetObjectIdPropertyValue(block, "DynamicBlockTableRecord");
                if (dynamicDefinitionId.IsNull || dynamicDefinitionId == block.BlockTableRecord)
                    return false;

                BlockTableRecord dynamicDefinition = tr.GetObject(dynamicDefinitionId, OpenMode.ForRead, false) as BlockTableRecord;
                if (dynamicDefinition == null)
                    return false;

                return !dynamicDefinition.IsAnonymous &&
                       !string.IsNullOrWhiteSpace(dynamicDefinition.Name) &&
                       !dynamicDefinition.Name.StartsWith("*", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRegularBlockReference(Transaction tr, ObjectId id)
        {
            try
            {
                if (id.IsErased)
                    return false;

                BlockReference block = tr.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                if (block == null || block.IsErased || block.BlockTableRecord.IsNull)
                    return false;

                if (HasNamedDynamicDefinition(tr, block))
                    return false;

                return !IsAnonymousBlockReference(tr, id);
            }
            catch
            {
                return false;
            }
        }

        private static Entity GetEntityOrNull(Transaction tr, ObjectId id)
        {
            try
            {
                if (id.IsNull || id.IsErased)
                    return null;

                return tr.GetObject(id, OpenMode.ForRead, false) as Entity;
            }
            catch
            {
                return null;
            }
        }

        private static DBObject GetDBObjectOrNull(Transaction tr, ObjectId id)
        {
            try
            {
                if (id.IsNull || id.IsErased)
                    return null;

                return tr.GetObject(id, OpenMode.ForRead, false);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsCogoPoint(DBObject entity)
        {
            if (entity == null)
                return false;

            RXClass rx = entity.GetRXClass();
            string dxfName = rx?.DxfName ?? string.Empty;
            string rxName = rx?.Name ?? string.Empty;
            string typeName = entity.GetType().FullName ?? entity.GetType().Name;
            return string.Equals(dxfName, "AECC_COGO_POINT", StringComparison.OrdinalIgnoreCase) ||
                   rxName.IndexOf("CogoPoint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   rxName.IndexOf("AeccDbCogoPoint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("CogoPoint", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsSurveyNetwork(DBObject obj)
        {
            if (obj == null)
                return false;

            RXClass rx = obj.GetRXClass();
            string dxfName = rx?.DxfName ?? string.Empty;
            string rxName = rx?.Name ?? string.Empty;
            string typeName = obj.GetType().FullName ?? obj.GetType().Name;

            return (dxfName.IndexOf("SURVEY", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    dxfName.IndexOf("NETWORK", StringComparison.OrdinalIgnoreCase) >= 0) ||
                   rxName.IndexOf("SurveyNetwork", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   rxName.IndexOf("AeccSurveyNetworkEntity", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("SurveyNetwork", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("AeccSurveyNetworkEntity", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static TextInfo ReadTextInfo(Entity entity)
        {
            if (entity is DBText dbText)
            {
                return new TextInfo(dbText.TextString, dbText.Position, dbText.Height, dbText.Rotation, dbText.Layer);
            }

            if (entity is MText mText)
            {
                return new TextInfo(mText.Contents, mText.Location, mText.TextHeight, mText.Rotation, mText.Layer);
            }

            return null;
        }

        private static bool TrySetStringProperty(object target, string propertyName, string value)
        {
            if (target == null)
                return false;

            try
            {
                PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite || property.PropertyType != typeof(string))
                    return false;

                property.SetValue(target, value, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetPoint3dProperty(object target, Point3d value, params string[] propertyNames)
        {
            if (target == null || propertyNames == null)
                return false;

            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite || property.PropertyType != typeof(Point3d))
                        continue;

                    property.SetValue(target, value, null);
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TrySetBooleanProperty(object target, bool value, params string[] propertyNames)
        {
            if (target == null || propertyNames == null)
                return false;

            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }

                    FieldInfo field = target.GetType().GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    if (field != null && field.FieldType == typeof(bool))
                    {
                        field.SetValue(target, value);
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TrySetDoubleProperty(object target, double value, params string[] propertyNames)
        {
            if (target == null || propertyNames == null)
                return false;

            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite && property.PropertyType == typeof(double))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }

                    FieldInfo field = target.GetType().GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    if (field != null && field.FieldType == typeof(double))
                    {
                        field.SetValue(target, value);
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TrySetLongProperty(object target, long value, params string[] propertyNames)
        {
            if (target == null || propertyNames == null)
                return false;

            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite)
                    {
                        object converted = Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture);
                        property.SetValue(target, converted, null);
                        return true;
                    }

                    FieldInfo field = target.GetType().GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    if (field != null)
                    {
                        object converted = Convert.ChangeType(value, field.FieldType, CultureInfo.InvariantCulture);
                        field.SetValue(target, converted);
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TrySetPropertyValue(object target, string propertyName, object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(propertyName) || value == null)
                return false;

            try
            {
                PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanWrite && property.PropertyType.IsInstanceOfType(value))
                {
                    property.SetValue(target, value, null);
                    return true;
                }

                FieldInfo field = target.GetType().GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (field == null || !field.FieldType.IsInstanceOfType(value))
                    return false;

                field.SetValue(target, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetDoglegDirection(MLeader leader, int leaderIndex, Vector3d direction)
        {
            if (leader == null)
                return false;

            try
            {
                MethodInfo method = leader.GetType().GetMethod("SetDoglegDirection", new[] { typeof(int), typeof(Vector3d) }) ??
                                    leader.GetType().GetMethod("SetDogleg", new[] { typeof(int), typeof(Vector3d) });
                if (method == null)
                    return false;

                method.Invoke(leader, new object[] { leaderIndex, direction });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryInvokeParameterless(object target, string methodName)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName))
                return false;

            try
            {
                MethodInfo method = target.GetType().GetMethod(methodName, Type.EmptyTypes);
                if (method == null)
                    return false;

                method.Invoke(target, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetObjectIdProperty(object target, ObjectId value, params string[] propertyNames)
        {
            if (target == null || value.IsNull)
                return false;

            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite || property.PropertyType != typeof(ObjectId))
                        continue;

                    property.SetValue(target, value, null);
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool MatchesAnyWildcard(string value, IEnumerable<string> patterns)
        {
            if (string.IsNullOrWhiteSpace(value) || patterns == null)
                return false;

            foreach (string pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;

                string regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                if (Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return true;
            }

            return false;
        }

        private static bool HasWildcardRules(IEnumerable<string> patterns)
        {
            return patterns != null && patterns.Any(pattern => !string.IsNullOrWhiteSpace(pattern));
        }

        private static Point3d ToTargetZ(Point3d point, double targetElevation)
        {
            return new Point3d(point.X, point.Y, targetElevation);
        }

        private enum FbkPrepScope
        {
            Canceled,
            EntireDrawing,
            Selection
        }

        private sealed class TextInfo
        {
            public TextInfo(string contents, Point3d position, double height, double rotation, string layer)
            {
                Contents = contents ?? string.Empty;
                Position = position;
                Height = height;
                Rotation = rotation;
                Layer = layer;
            }

            public string Contents { get; }
            public Point3d Position { get; }
            public double Height { get; }
            public double Rotation { get; }
            public string Layer { get; }
        }

        private sealed class CfbkRunResult
        {
            public string LogFolder { get; set; }
            public string SourceFolder { get; set; }
            public string Filter { get; set; }
            public bool ImportCogoPoints { get; set; }
            public DateTime StartedUtc { get; set; }
            public List<CfbkImportedDrawing> Drawings { get; } = new List<CfbkImportedDrawing>();
            public int ImportedDrawingCount => Drawings.Count(drawing => drawing.ImportedUtc.HasValue && string.IsNullOrWhiteSpace(drawing.ImportError));
            public int ReimportedDrawingCount => Drawings.Count(drawing => drawing.WasReimported && drawing.ImportedUtc.HasValue && string.IsNullOrWhiteSpace(drawing.ImportError));
            public int AlreadyImportedCount => Drawings.Count(drawing => string.Equals(drawing.SkipReason, "Already imported and unchanged", StringComparison.OrdinalIgnoreCase));
            public int LegacyImportRecordCount => Drawings.Count(drawing => string.Equals(drawing.SkipReason, "Already imported by legacy record", StringComparison.OrdinalIgnoreCase));
            public int ImportFailureCount => Drawings.Count(drawing => !string.IsNullOrWhiteSpace(drawing.ImportError));
            public int IgnoredDrawingCount => Drawings.Count(drawing =>
                !string.IsNullOrWhiteSpace(drawing.SkipReason) &&
                !string.Equals(drawing.SkipReason, "Already imported and unchanged", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(drawing.SkipReason, "Already imported by legacy record", StringComparison.OrdinalIgnoreCase));
            public int DeletedPreviousEntityCount => Drawings.Sum(drawing => drawing.DeletedPreviousEntityCount);
            public int ImportedEntityCount => Drawings.Sum(drawing => drawing.ImportedEntityCount);
            public int SkippedEntityCount => Drawings.Sum(drawing => drawing.SkippedEntityCount);
            public int ImportedCogoPointCount => Drawings.Sum(drawing => drawing.ImportedCogoPointCount);
            public int DuplicateCogoPointCount => Drawings.Sum(drawing => drawing.DuplicateCogoPointCount);
            public int CogoPointImportFailureCount => Drawings.Sum(drawing => drawing.CogoPointImportFailureCount);
        }

        private sealed class CfbkImportedDrawing
        {
            public CfbkImportedDrawing(string sourcePath)
            {
                SourcePath = sourcePath;
            }

            public string SourcePath { get; }
            public int ImportedEntityCount { get; set; }
            public int SkippedEntityCount { get; set; }
            public int ImportedCogoPointCount { get; set; }
            public int DuplicateCogoPointCount { get; set; }
            public int CogoPointImportFailureCount { get; set; }
            public string SkipReason { get; set; }
            public string ImportError { get; set; }
            public long FileSizeBytes { get; set; }
            public DateTime LastWriteUtc { get; set; }
            public DateTime? ImportedUtc { get; set; }
            public DateTime? PreviousImportUtc { get; set; }
            public bool WasReimported { get; set; }
            public int DeletedPreviousEntityCount { get; set; }
            public List<string> DestinationHandles { get; } = new List<string>();
            public Dictionary<string, int> SkippedObjectTypes { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public string StatusText
            {
                get
                {
                    if (!string.IsNullOrWhiteSpace(ImportError))
                        return "Failed";
                    if (!string.IsNullOrWhiteSpace(SkipReason))
                        return SkipReason;
                    if (ImportedUtc.HasValue)
                        return WasReimported ? "Reimported" : "Imported";
                    return "Pending";
                }
            }
        }

        private sealed class CfbkIgnoredDrawing
        {
            public CfbkIgnoredDrawing(string sourcePath, string reason)
            {
                SourcePath = sourcePath;
                Reason = reason;
            }

            public string SourcePath { get; }
            public string Reason { get; }
        }

        private sealed class CfbkSourcePlan
        {
            public List<string> MatchingDrawings { get; } = new List<string>();
            public List<CfbkIgnoredDrawing> IgnoredDrawings { get; } = new List<CfbkIgnoredDrawing>();
        }

        private sealed class CfbkImportRecord
        {
            public CfbkImportRecord(string normalizedPath, DateTime importedUtc, long fileSizeBytes, DateTime lastWriteUtc, IEnumerable<string> destinationHandles)
            {
                NormalizedPath = normalizedPath;
                ImportedUtc = importedUtc;
                FileSizeBytes = fileSizeBytes;
                LastWriteUtc = lastWriteUtc;
                DestinationHandles = destinationHandles == null ? new List<string>() : destinationHandles.Where(handle => !string.IsNullOrWhiteSpace(handle)).ToList();
            }

            public string NormalizedPath { get; }
            public DateTime ImportedUtc { get; }
            public long FileSizeBytes { get; }
            public DateTime LastWriteUtc { get; }
            public List<string> DestinationHandles { get; }

            public bool CanRefresh => FileSizeBytes >= 0 && LastWriteUtc != DateTime.MinValue;

            public bool Matches(long fileSizeBytes, DateTime lastWriteUtc)
            {
                return FileSizeBytes == fileSizeBytes && LastWriteUtc == lastWriteUtc;
            }
        }

        private sealed class CfbkCogoPointSignature
        {
            public HashSet<string> Keys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class CfbkCogoPointData
        {
            public CfbkCogoPointData(Point3d location, long? pointNumber, string pointName, string rawDescription)
            {
                Location = location;
                PointNumber = pointNumber;
                PointName = pointName ?? string.Empty;
                RawDescription = rawDescription ?? string.Empty;
            }

            public Point3d Location { get; }
            public long? PointNumber { get; }
            public string PointName { get; }
            public string RawDescription { get; }
        }

        private sealed class ScanGridBoundary
        {
            public ScanGridBoundary(List<Point2d> points, double elevation)
            {
                Points = points;
                Elevation = elevation;
            }

            public List<Point2d> Points { get; }
            public double Elevation { get; }
        }

        private sealed class ScanGridSegment
        {
            public ScanGridSegment(Point2d start, Point2d end)
            {
                Start = start;
                End = end;
            }

            public Point2d Start { get; }
            public Point2d End { get; }
        }

        private sealed class FbkPrepResult
        {
            public int CogoPointsFound { get; set; }
            public int CogoDisplayObjectsCreated { get; set; }
            public int SurveyNetworksDeleted { get; set; }
            public int BlockReferencesExploded { get; set; }
            public int AnonymousBlocksBurst { get; set; }
            public int CogoLayerLinesDeleted { get; set; }
            public int LinesConvertedTo3dPolylines { get; set; }
            public int TinyTextDeleted { get; set; }
            public int TextDeletedByLayer { get; set; }
            public int TextKeptByLayer { get; set; }
            public int TextConvertedToMleaders { get; set; }
            public int AnnotationObjectsFlattened { get; set; }
            public int BlocksFlattened { get; set; }
            public int ObjectsSetByLayer { get; set; }
            public int BlocksSkippedByFlattenRule { get; set; }
            public int CogoPointsRestyled { get; set; }
            public int CogoPointStyleSkipped { get; set; }
            public List<string> Errors { get; } = new List<string>();
        }
    }
}
