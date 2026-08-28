using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
        private const string Version = "0.1.22";

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
                    ed.WriteMessage("\n  Extracted graphics deleted by layer rule: {0}", result.ExtractedGraphicsDeletedByLayer);
                    ed.WriteMessage("\n  COGO point layer objects skipped: {0}", result.ProtectedSourceObjectsSkipped);
                    ed.WriteMessage("\n  Lines converted to 3D polylines: {0}", result.LinesConvertedTo3dPolylines);
                    ed.WriteMessage("\n  Tiny text deleted: {0}", result.TinyTextDeleted);
                    ed.WriteMessage("\n  Text/MText deleted by layer: {0}", result.TextDeletedByLayer);
                    ed.WriteMessage("\n  Text/MText kept by layer: {0}", result.TextKeptByLayer);
                    ed.WriteMessage("\n  Text/MText converted to MLeaders: {0}", result.TextConvertedToMleaders);
                    ed.WriteMessage("\n  Annotation objects flattened: {0}", result.ObjectsFlattened);
                    ed.WriteMessage("\n  Blocks skipped by flatten rule: {0}", result.BlocksSkippedByFlattenRule);
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

        [CommandMethod("DSVERSION", CommandFlags.Modal)]
        public void VersionInfo()
        {
            PrintLoadMessage(Application.DocumentManager.MdiActiveDocument?.Editor);
        }

        [CommandMethod("DSSETTINGS", CommandFlags.Session)]
        public void OpenSettings()
        {
            DraftingSuiteSettingsForm.ShowSettingsDialog();
        }

        internal static void PrintLoadMessage(Editor ed)
        {
            if (ed == null)
                return;

            ed.WriteMessage("\nDrafting Suite v{0} loaded. Commands: DS, DSFBKPREP, DSSETTINGS, DSVERSION.", Version);
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

                ExplodeBlockReferences(db, tr, createdIds, result, settings.ExplodePassesBeforeBurst, "before burst");

                if (settings.BurstInserts)
                    BurstAnonymousBlocks(db, tr, createdIds, result, settings.MaxAnonymousBurstPasses);

                ExplodeBlockReferences(db, tr, createdIds, result, settings.ExplodePassesAfterBurst, "after burst");

                PruneExtractedGraphicsByLayer(tr, createdIds, result, settings);

                if (settings.ConvertLinesTo3dPolylines)
                    ConvertLinesTo3dPolylines(tr, candidateIds, result, settings);

                List<ObjectId> annotationIds = createdIds
                    .Where(id => IsDraftingAnnotationForSettings(GetEntityOrNull(tr, id), settings, false, result))
                    .ToList();
                annotationIds.AddRange(candidateIds.Where(id => IsDraftingAnnotationForSettings(GetEntityOrNull(tr, id), settings, true, result)));

                DeleteTinyText(tr, annotationIds, result, settings.TinyTextDeleteHeight);

                if (settings.ConvertTextToMleaders)
                    ConvertTextToMleaders(db, tr, annotationIds, result, settings);

                if (settings.FlattenAnnotation)
                {
                    List<ObjectId> flattenIds = CollectFlattenIds(candidateIds, createdIds, db, tr, settings, result);
                    FlattenObjects(tr, flattenIds, result, settings);
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

                            entity.SetDatabaseDefaults(db);
                            ObjectId createdId = modelSpace.AppendEntity(entity);
                            tr.AddNewlyCreatedDBObject(entity, true);
                            if (knownIds.Add(createdId))
                                candidateIds.Add(createdId);
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

                            entity.SetDatabaseDefaults(db);
                            ObjectId createdId = modelSpace.AppendEntity(entity);
                            tr.AddNewlyCreatedDBObject(entity, true);
                            if (knownIds.Add(createdId))
                                candidateIds.Add(createdId);
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

        private static void ConvertTextToMleaders(Database db, Transaction tr, List<ObjectId> annotationIds, FbkPrepResult result, DraftingSuiteSettings settings)
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
                if (MatchesAnyWildcard(text.Layer, settings.MLeaderDeleteLayerPatterns))
                {
                    DeleteTextByLayerRule(entity, id, result);
                    continue;
                }
                if (MatchesAnyWildcard(text.Layer, settings.MLeaderKeepTextLayerPatterns))
                {
                    result.TextKeptByLayer++;
                    continue;
                }

                try
                {
                    entity.UpgradeOpen();
                    MLeader leader = CreateMLeader(db, text, settings);
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

        private static void PruneExtractedGraphicsByLayer(Transaction tr, List<ObjectId> createdIds, FbkPrepResult result, DraftingSuiteSettings settings)
        {
            bool hasResultRules = HasWildcardRules(settings.ResultLayerPatterns);
            bool hasAnnotationRules = HasWildcardRules(settings.AnnotationLayerPatterns);
            if (!hasResultRules && !hasAnnotationRules)
                return;

            foreach (ObjectId id in createdIds.ToList())
            {
                Entity entity = GetEntityOrNull(tr, id);
                if (entity == null || entity.IsErased)
                    continue;

                bool keepAsResult = hasResultRules && MatchesAnyWildcard(entity.Layer, settings.ResultLayerPatterns);
                bool keepAsAnnotation = hasAnnotationRules && MatchesAnyWildcard(entity.Layer, settings.AnnotationLayerPatterns);
                if (keepAsResult || keepAsAnnotation)
                    continue;

                try
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                    result.ExtractedGraphicsDeletedByLayer++;
                }
                catch (System.Exception ex)
                {
                    result.Errors.Add("Extracted graphic layer cleanup skipped " + id.Handle + ": " + ex.Message);
                }
            }
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

        private static void DeleteTinyText(Transaction tr, IEnumerable<ObjectId> annotationIds, FbkPrepResult result, double maxHeight)
        {
            if (maxHeight <= 0.0)
                return;

            foreach (ObjectId id in new HashSet<ObjectId>(annotationIds))
            {
                Entity entity = GetEntityOrNull(tr, id);
                TextInfo text = ReadTextInfo(entity);
                if (text == null || text.Height > maxHeight)
                    continue;

                try
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                    result.TinyTextDeleted++;
                }
                catch (System.Exception ex)
                {
                    result.Errors.Add("Tiny text delete skipped " + id.Handle + ": " + ex.Message);
                }
            }
        }

        private static MLeader CreateMLeader(Database db, TextInfo text, DraftingSuiteSettings settings)
        {
            Point3d arrowPoint = ToTargetZ(text.Position, settings.FlattenElevation);
            Point3d textPoint = new Point3d(arrowPoint.X + settings.MLeaderTextOffsetX, arrowPoint.Y + settings.MLeaderTextOffsetY, settings.FlattenElevation);
            MText mText = new MText
            {
                Contents = text.Contents,
                TextHeight = text.Height > 0.0 ? text.Height : db.Textsize,
                Location = textPoint,
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
            leader.AddFirstVertex(lineIndex, arrowPoint);
            leader.AddLastVertex(lineIndex, textPoint);
            return leader;
        }

        private static List<ObjectId> CollectFlattenIds(List<ObjectId> candidateIds, List<ObjectId> createdIds, Database db, Transaction tr, DraftingSuiteSettings settings, FbkPrepResult result)
        {
            HashSet<ObjectId> ids = new HashSet<ObjectId>(
                createdIds.Where(id => IsDraftingAnnotationForSettings(GetEntityOrNull(tr, id), settings, false, result)));
            foreach (ObjectId id in candidateIds)
            {
                Entity entity = GetEntityOrNull(tr, id);
                if (IsDraftingAnnotationForSettings(entity, settings, true, result))
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
                if (IsProtectedSourceEntity(line, settings, result))
                    continue;

                try
                {
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

        private static void FlattenObjects(Transaction tr, IEnumerable<ObjectId> ids, FbkPrepResult result, DraftingSuiteSettings settings)
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

                    if (FlattenEntity(entity, settings.FlattenElevation))
                        result.ObjectsFlattened++;
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

        private static bool FlattenEntity(Entity entity, double targetElevation)
        {
            if (!IsDraftingAnnotation(entity))
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

            FlattenByExtents(entity, targetElevation);
            return true;
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
                return property == null ? null : property.GetValue(target, null);
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

        private static bool IsDraftingAnnotationForSettings(Entity entity, DraftingSuiteSettings settings, bool sourceEntity, FbkPrepResult result)
        {
            if (!IsDraftingAnnotation(entity))
                return false;

            if (sourceEntity && IsProtectedSourceEntity(entity, settings, result))
                return false;

            return MatchesOptionalWildcardRules(entity.Layer, settings.AnnotationLayerPatterns);
        }

        private static bool IsProtectedSourceEntity(Entity entity, DraftingSuiteSettings settings, FbkPrepResult result)
        {
            if (entity == null || !HasWildcardRules(settings.ProtectedSourceLayerPatterns))
                return false;
            if (!MatchesAnyWildcard(entity.Layer, settings.ProtectedSourceLayerPatterns))
                return false;

            result.RecordProtectedSourceObject(entity.ObjectId);
            return true;
        }

        private static bool MatchesOptionalWildcardRules(string value, IEnumerable<string> patterns)
        {
            return !HasWildcardRules(patterns) || MatchesAnyWildcard(value, patterns);
        }

        private static bool HasWildcardRules(IEnumerable<string> patterns)
        {
            return patterns != null && patterns.Any(pattern => !string.IsNullOrWhiteSpace(pattern));
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

                BlockTableRecord definition = tr.GetObject(block.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                if (definition == null || definition.IsFromExternalReference || definition.IsLayout)
                    return false;

                return definition.IsAnonymous ||
                       (!string.IsNullOrEmpty(definition.Name) && definition.Name.StartsWith("*", StringComparison.Ordinal));
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

            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite || property.PropertyType != typeof(string))
                return false;

            property.SetValue(target, value, null);
            return true;
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

        private sealed class FbkPrepResult
        {
            public int CogoPointsFound { get; set; }
            public int CogoDisplayObjectsCreated { get; set; }
            public int SurveyNetworksDeleted { get; set; }
            public int BlockReferencesExploded { get; set; }
            public int AnonymousBlocksBurst { get; set; }
            public int ExtractedGraphicsDeletedByLayer { get; set; }
            public int ProtectedSourceObjectsSkipped { get; private set; }
            public int LinesConvertedTo3dPolylines { get; set; }
            public int TinyTextDeleted { get; set; }
            public int TextDeletedByLayer { get; set; }
            public int TextKeptByLayer { get; set; }
            public int TextConvertedToMleaders { get; set; }
            public int ObjectsFlattened { get; set; }
            public int BlocksSkippedByFlattenRule { get; set; }
            public int CogoPointsRestyled { get; set; }
            public int CogoPointStyleSkipped { get; set; }
            public List<string> Errors { get; } = new List<string>();
            private HashSet<ObjectId> ProtectedSourceObjectIds { get; } = new HashSet<ObjectId>();

            public void RecordProtectedSourceObject(ObjectId id)
            {
                if (id.IsNull || !ProtectedSourceObjectIds.Add(id))
                    return;

                ProtectedSourceObjectsSkipped++;
            }
        }
    }
}
