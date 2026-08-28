using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

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
        private const string Version = "0.1.0";
        private const string StandardStyleName = "Standard";
        private const double TextTargetSearchDistance = 75.0;

        [CommandMethod("DS", CommandFlags.Session)]
        public void OpenPalette()
        {
            DraftingSuitePalette.Show();
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

                using (doc.LockDocument())
                {
                    FbkPrepResult result = RunFbkPrep(doc.Database, ed, scope);
                    ed.WriteMessage("\nFBK Prep complete.");
                    ed.WriteMessage("\n  Scope: {0}", scope == FbkPrepScope.Selection ? "selection" : "entire drawing");
                    ed.WriteMessage("\n  COGO points found: {0}", result.CogoPointsFound);
                    ed.WriteMessage("\n  COGO display objects created: {0}", result.CogoDisplayObjectsCreated);
                    ed.WriteMessage("\n  Text/MText converted to MLeaders: {0}", result.TextConvertedToMleaders);
                    ed.WriteMessage("\n  Annotation objects flattened: {0}", result.ObjectsFlattened);
                    ed.WriteMessage("\n  COGO points restyled: {0}", result.CogoPointsRestyled);
                    if (result.CogoPointStyleSkipped > 0)
                        ed.WriteMessage("\n  COGO style changes skipped: {0}", result.CogoPointStyleSkipped);
                    if (result.TextSkippedNoTarget > 0)
                        ed.WriteMessage("\n  Text/MText skipped with no nearby target: {0}", result.TextSkippedNoTarget);
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

        [CommandMethod("DSVERSION", CommandFlags.Modal)]
        public void VersionInfo()
        {
            PrintLoadMessage(Application.DocumentManager.MdiActiveDocument?.Editor);
        }

        internal static void PrintLoadMessage(Editor ed)
        {
            if (ed == null)
                return;

            ed.WriteMessage("\nDrafting Suite v{0} loaded. Commands: DS, DSFBKPREP, DSVERSION.", Version);
            ed.WriteMessage("\n");
        }

        private static FbkPrepScope PromptScope(Editor ed)
        {
            PromptKeywordOptions options = new PromptKeywordOptions("\nFBK Prep scope [Entire drawing/Selection] <Entire>: ");
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

        private static FbkPrepResult RunFbkPrep(Database db, Editor ed, FbkPrepScope scope)
        {
            FbkPrepResult result = new FbkPrepResult();
            List<ObjectId> selectedIds = scope == FbkPrepScope.Selection ? PromptSelection(ed) : new List<ObjectId>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<ObjectId> candidateIds = scope == FbkPrepScope.Selection
                    ? selectedIds
                    : CollectModelSpaceEntityIds(db, tr);

                List<CogoTarget> targets = new List<CogoTarget>();
                List<ObjectId> cogoIds = new List<ObjectId>();
                foreach (ObjectId id in candidateIds)
                {
                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (!IsCogoPoint(entity))
                        continue;

                    cogoIds.Add(id);
                    result.CogoPointsFound++;
                    if (TryGetPoint(entity, out Point3d point))
                        targets.Add(new CogoTarget(id, point));
                }

                List<ObjectId> createdIds = ExplodeCogoDisplayGraphics(db, tr, cogoIds, result);
                AppendCreatedBlockTargets(tr, createdIds, targets);

                List<ObjectId> annotationIds = new List<ObjectId>(createdIds);
                annotationIds.AddRange(candidateIds.Where(id => IsDraftingAnnotation(tr.GetObject(id, OpenMode.ForRead, false) as Entity)));

                ConvertTextToMleaders(db, tr, annotationIds, targets, result);

                List<ObjectId> flattenIds = CollectFlattenIds(candidateIds, createdIds, db, tr);
                FlattenObjects(tr, flattenIds, result);

                RestyleCogoPoints(tr, cogoIds, result);

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

                        entity.SetDatabaseDefaults(db);
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

        private static void AppendCreatedBlockTargets(Transaction tr, IEnumerable<ObjectId> ids, List<CogoTarget> targets)
        {
            foreach (ObjectId id in ids)
            {
                BlockReference block = tr.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                if (block != null)
                    targets.Add(new CogoTarget(id, block.Position));
            }
        }

        private static void ConvertTextToMleaders(Database db, Transaction tr, List<ObjectId> annotationIds, List<CogoTarget> targets, FbkPrepResult result)
        {
            if (targets.Count == 0)
                return;

            BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            HashSet<ObjectId> uniqueIds = new HashSet<ObjectId>(annotationIds);

            foreach (ObjectId id in uniqueIds.ToList())
            {
                Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                TextInfo text = ReadTextInfo(entity);
                if (text == null)
                    continue;

                CogoTarget target = FindNearestTarget(text.Position, targets);
                if (target == null || target.Point.DistanceTo(text.Position) > TextTargetSearchDistance)
                {
                    result.TextSkippedNoTarget++;
                    continue;
                }

                try
                {
                    entity.UpgradeOpen();
                    MLeader leader = CreateMLeader(db, text, target.Point);
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

        private static MLeader CreateMLeader(Database db, TextInfo text, Point3d target)
        {
            MText mText = new MText
            {
                Contents = text.Contents,
                TextHeight = text.Height > 0.0 ? text.Height : db.Textsize,
                Location = ToZeroZ(text.Position),
                Rotation = text.Rotation,
                Attachment = AttachmentPoint.MiddleLeft
            };

            MLeader leader = new MLeader();
            leader.SetDatabaseDefaults(db);
            leader.Layer = text.Layer;
            leader.ContentType = ContentType.MTextContent;
            leader.MText = mText;
            int leaderIndex = leader.AddLeader();
            int lineIndex = leader.AddLeaderLine(leaderIndex);
            leader.AddFirstVertex(lineIndex, ToZeroZ(target));
            leader.AddLastVertex(lineIndex, ToZeroZ(text.Position));
            return leader;
        }

        private static List<ObjectId> CollectFlattenIds(List<ObjectId> candidateIds, List<ObjectId> createdIds, Database db, Transaction tr)
        {
            HashSet<ObjectId> ids = new HashSet<ObjectId>(createdIds);
            foreach (ObjectId id in candidateIds)
            {
                Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (IsDraftingAnnotation(entity))
                    ids.Add(id);
            }

            return ids.ToList();
        }

        private static void FlattenObjects(Transaction tr, IEnumerable<ObjectId> ids, FbkPrepResult result)
        {
            foreach (ObjectId id in ids)
            {
                Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity == null || entity.IsErased)
                    continue;

                try
                {
                    if (FlattenEntity(entity))
                        result.ObjectsFlattened++;
                }
                catch (System.Exception ex)
                {
                    result.Errors.Add("Flatten skipped " + id.Handle + ": " + ex.Message);
                }
            }
        }

        private static bool FlattenEntity(Entity entity)
        {
            if (!IsDraftingAnnotation(entity))
                return false;

            entity.UpgradeOpen();
            if (entity is BlockReference block)
            {
                block.Position = ToZeroZ(block.Position);
                return true;
            }
            if (entity is DBText dbText)
            {
                dbText.Position = ToZeroZ(dbText.Position);
                if (!dbText.AlignmentPoint.IsEqualTo(Point3d.Origin))
                    dbText.AlignmentPoint = ToZeroZ(dbText.AlignmentPoint);
                return true;
            }
            if (entity is MText mText)
            {
                mText.Location = ToZeroZ(mText.Location);
                return true;
            }
            if (entity is MLeader mLeader)
            {
                FlattenByExtents(mLeader);
                return true;
            }
            if (entity is Leader leader)
            {
                FlattenByExtents(leader);
                return true;
            }
            if (entity is Dimension dimension)
            {
                FlattenByExtents(dimension);
                return true;
            }

            FlattenByExtents(entity);
            return true;
        }

        private static void FlattenByExtents(Entity entity)
        {
            try
            {
                Extents3d extents = entity.GeometricExtents;
                double z = Math.Abs(extents.MinPoint.Z) <= Math.Abs(extents.MaxPoint.Z) ? extents.MinPoint.Z : extents.MaxPoint.Z;
                if (Math.Abs(z) > 1e-8)
                    entity.TransformBy(Matrix3d.Displacement(new Vector3d(0.0, 0.0, -z)));
            }
            catch
            {
            }
        }

        private static void RestyleCogoPoints(Transaction tr, IEnumerable<ObjectId> cogoIds, FbkPrepResult result)
        {
            foreach (ObjectId id in cogoIds)
            {
                Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (!IsCogoPoint(entity))
                    continue;

                try
                {
                    entity.UpgradeOpen();
                    bool pointStyleSet = TrySetStringProperty(entity, "StyleName", StandardStyleName) ||
                                         TrySetStringProperty(entity, "PointStyleName", StandardStyleName);
                    bool labelStyleSet = TrySetStringProperty(entity, "LabelStyleName", StandardStyleName) ||
                                         TrySetStringProperty(entity, "PointLabelStyleName", StandardStyleName);
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

        private static bool IsCogoPoint(Entity entity)
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

        private static CogoTarget FindNearestTarget(Point3d point, List<CogoTarget> targets)
        {
            CogoTarget nearest = null;
            double nearestDistance = double.MaxValue;
            foreach (CogoTarget target in targets)
            {
                double distance = point.DistanceTo(target.Point);
                if (distance >= nearestDistance)
                    continue;

                nearest = target;
                nearestDistance = distance;
            }

            return nearest;
        }

        private static bool TryGetPoint(object value, out Point3d point)
        {
            point = Point3d.Origin;
            if (value == null)
                return false;

            foreach (string name in new[] { "Location", "Position" })
            {
                object property = ReadProperty(value, name);
                if (property is Point3d point3d)
                {
                    point = point3d;
                    return true;
                }
            }

            double? easting = ReadDoubleProperty(value, "Easting");
            double? northing = ReadDoubleProperty(value, "Northing");
            double elevation = ReadDoubleProperty(value, "Elevation") ?? 0.0;
            if (easting.HasValue && northing.HasValue)
            {
                point = new Point3d(easting.Value, northing.Value, elevation);
                return true;
            }

            return false;
        }

        private static bool TrySetStringProperty(object target, string propertyName, string value)
        {
            if (target == null)
                return false;

            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite || property.PropertyType != typeof(string))
                return false;

            property.SetValue(target, value, null);
            return true;
        }

        private static object ReadProperty(object value, string name)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return property?.GetValue(value, null);
            }
            catch
            {
                return null;
            }
        }

        private static double? ReadDoubleProperty(object value, string name)
        {
            object property = ReadProperty(value, name);
            if (property == null)
                return null;

            try
            {
                return Convert.ToDouble(property, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static Point3d ToZeroZ(Point3d point)
        {
            return new Point3d(point.X, point.Y, 0.0);
        }

        private enum FbkPrepScope
        {
            Canceled,
            EntireDrawing,
            Selection
        }

        private sealed class CogoTarget
        {
            public CogoTarget(ObjectId id, Point3d point)
            {
                Id = id;
                Point = point;
            }

            public ObjectId Id { get; }
            public Point3d Point { get; }
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

        private sealed class FbkPrepResult
        {
            public int CogoPointsFound { get; set; }
            public int CogoDisplayObjectsCreated { get; set; }
            public int TextConvertedToMleaders { get; set; }
            public int TextSkippedNoTarget { get; set; }
            public int ObjectsFlattened { get; set; }
            public int CogoPointsRestyled { get; set; }
            public int CogoPointStyleSkipped { get; set; }
            public List<string> Errors { get; } = new List<string>();
        }
    }
}
