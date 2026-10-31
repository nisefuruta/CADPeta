using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadPeta.Geometry;
#if CADPETA_ACAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using ProductPluginDefaults = CadPeta.Plugin.Acad.AcadPluginDefaults;
using CadTransaction = CadPeta.Acquire.Acad.CadTransaction;
namespace CadPeta.Plugin.Acad;
#elif CADPETA_ARES
using Teigha.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using CadRuntimeException = Teigha.Runtime.Exception;
using ProductPluginDefaults = CadPeta.Plugin.Ares.AresPluginDefaults;
using CadTransaction = CadPeta.Acquire.Ares.CadTransaction;
namespace CadPeta.Plugin.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.ApplicationServices;
using Itjp.Ijcad.DatabaseServices;
using Itjp.Ijcad.Geometry;
using CadRuntimeException = Itjp.Ijcad.Runtime.Exception;
using ProductPluginDefaults = CadPeta.Plugin.Ijcad.IjcadPluginDefaults;
using CadTransaction = CadPeta.Acquire.Ijcad.CadTransaction;
namespace CadPeta.Plugin.Ijcad;
#elif CADPETA_BRICSCAD
using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using CadRuntimeException = Teigha.Runtime.Exception;
using ProductPluginDefaults = CadPeta.Plugin.Bricscad.BricscadPluginDefaults;
using CadTransaction = CadPeta.Acquire.Bricscad.CadTransaction;
namespace CadPeta.Plugin.Bricscad;
#else
#error CAD product symbol is required
#endif
using CadPeta.Acquire.Common;
using CadPeta.Plugin.Shared;
using System.Globalization;


// 選択絞り込みポート: 選択オブジェクトだけを側DBへ複製し、元レイアウトのページ設定を引き継いだ
// 側DBレイアウトを対象とする範囲プロットポート（製品 PlotPort）を提供する。選択に通常Viewportを
// 含む場合は、Viewport固有設定を保つため元レイアウトを未確定Transaction内で一時隔離して印刷する。
// 範囲・尺度・クロップの判断は通常経路と同じ PlotOrchestrator が行い、永続的な変更は残さない。
internal sealed class CadSelectionPlotPort : ISelectionPlotPort
{
    readonly Document _doc;
    public CadSelectionPlotPort(Document doc) => _doc = doc;

    public ISelectionPlotSession Begin(object selectionHandle,
        TextWriter log, TextWriter verbose) =>
        new Session(_doc, (ObjectId[])selectionHandle, log, verbose);

    sealed class Session : ISelectionPlotSession
    {
        const string NonPlotLayerName = "Defpoints";
        // 明示選択された Defpoints 図形だけを印刷可能にする側DB専用レイヤ。
        const string PlotEnabledLayerName = "CADPETA_PLOT_DEFPOINTS";
        // SupportedCount==0（空の選択）では側DB一式を生成しないため null のままになり得る。
        // Plotter はこの経路で使われない不変条件（SelectionPlotOrchestrator が
        // SupportedCount==0 で中止する）の下で null 非対応のまま扱ってよい
        Database? _sideDb;
        readonly Database _sourceDb;
        readonly Document _sourceDoc;
        CadTransaction? _sourceIsolation;
        IsolationSnapshot? _isolationSnapshot;
        bool _disposed;

        public int SelectedCount { get; }
        public int SupportedCount { get; }
        public IReadOnlyList<UnsupportedSelectionItem> Unsupported { get; }
        public IPlotPort Plotter { get; } = null!;

        public Session(Document doc, ObjectId[] ids, TextWriter log, TextWriter verbose)
        {
            _sourceDb = doc.Database;
            _sourceDoc = doc;
            SelectedCount = ids.Length;
            var supported = new ObjectIdCollection();
            var viewportClipIds = new ObjectIdCollection();
            try
            {
                var unsupported = new List<UnsupportedSelectionItem>();
                PlotSettings sourceSettings;
                UnitsValue sourceUnits;
                bool sourcePlotStyleMode;
                bool sourceIsModelSpace;
                bool hasSelectedViewport = false;
                string sourceLayoutName;
                using (var tr = new CadTransaction(doc.Database))
                {
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    ObjectId currentSpaceId = doc.Database.CurrentSpaceId;
                    ObjectId modelId = bt[BlockTableRecord.ModelSpace];
                    sourceIsModelSpace = currentSpaceId == modelId;
                    var currentSpace = (BlockTableRecord)tr.GetObject(currentSpaceId, OpenMode.ForRead);
                    foreach (var id in ids)
                    {
                        // Kind（Invalid 等）は言語不変の識別子、Reason は表示文言（言語カタログ）
                        if (!id.IsValid || id.IsErased) { unsupported.Add(new("Invalid", id.ToString(), Msg.Text("selplot.reason.invalid"))); continue; }
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) { unsupported.Add(new("NonEntity", id.Handle.ToString(), Msg.Text("selplot.reason.non_entity"))); continue; }
                        if (ent.OwnerId != currentSpaceId) { unsupported.Add(new("OtherSpace", id.Handle.ToString(), Msg.Text("selplot.reason.other_space"))); continue; }
                        if (ent is Viewport viewport)
                        {
                            var disposition = SelectionViewportPolicy.Classify(sourceIsModelSpace,
                                viewport.Number, viewport.On,
                                ProductPluginDefaults.PaperSpaceViewportNumber);
                            if (disposition != SelectionViewportDisposition.Supported)
                            {
                                string reasonKey = disposition switch
                                {
                                    SelectionViewportDisposition.ModelSpace => "selplot.reason.viewport_model_space",
                                    SelectionViewportDisposition.Controller => "selplot.reason.viewport_controller",
                                    SelectionViewportDisposition.Off => "selplot.reason.viewport_off",
                                    _ => throw new InvalidOperationException("未定義のViewport選択分類"),
                                };
                                unsupported.Add(new(disposition.ToString(), id.Handle.ToString(),
                                    Msg.Text(reasonKey)));
                                continue;
                            }
                            if (viewport.NonRectClipOn &&
                                !viewport.NonRectClipEntityId.IsNull &&
                                viewport.NonRectClipEntityId.IsValid &&
                                !viewport.NonRectClipEntityId.IsErased)
                                viewportClipIds.Add(viewport.NonRectClipEntityId);
                            hasSelectedViewport = true;
                        }
                        if (ent is BlockReference br)
                        {
                            var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                            if (btr.IsFromExternalReference || btr.IsFromOverlayReference)
                            { unsupported.Add(new("Xref", id.Handle.ToString(), Msg.Text("selplot.reason.xref"))); continue; }
                        }
                        supported.Add(id);
                    }
                    var layout = (Layout)tr.GetObject(currentSpace.LayoutId, OpenMode.ForRead);
                    sourceLayoutName = layout.LayoutName;
                    sourceSettings = new PlotSettings(layout.ModelType);
                    try
                    {
                        sourceSettings.CopyFrom(layout);
                        sourceUnits = doc.Database.Insunits;
                        sourcePlotStyleMode = doc.Database.PlotStyleMode;
                    }
                    catch
                    {
                        sourceSettings.Dispose();
                        throw;
                    }
                }
                using (sourceSettings)
                {
                    Unsupported = unsupported; SupportedCount = supported.Count;
                    if (SupportedCount == 0)
                    {
                        // 側DB一式は使われないため生成しない。Plotter はこの経路で参照されない
                        // 不変条件（SelectionPlotOrchestrator が SupportedCount==0 で中止する）
                        // を前提に null のままにする
                        return;
                    }

                    if (!sourceIsModelSpace && hasSelectedViewport)
                    {
                        // throwし得るPlotPort構築を先に完了し、Transaction取得後は代入だけにする。
                        var plotter = new CadPlotPort(doc);
                        _sourceIsolation = BeginPaperViewportIsolation(doc.Database, supported,
                            viewportClipIds, out _isolationSnapshot);
                        Plotter = plotter;
                        return;
                    }

                    try
                    {
                        string creationMethod;
                        ObjectId targetLayoutId;
                        ObjectId targetBtrId;
                        IReadOnlyList<ObjectId>? paperSelectedIds = null;
                        if (sourceIsModelSpace)
                        {
                            var sourceSnapshot = CaptureSelectionMetadata(doc.Database, supported);
                            _sideDb = CreateSideDatabase(doc.Database, supported, sourceSnapshot, log,
                                out creationMethod);
                            (targetLayoutId, targetBtrId) = GetModelLayout(_sideDb);
                        }
                        else
                        {
                            _sideDb = CreatePaperSideDatabase(doc.Database, sourceLayoutName, supported,
                                out targetLayoutId, out targetBtrId,
                                out paperSelectedIds);
                            creationMethod = "full-Wblock+paper-prune";
                        }
                        string? dumpStem = SideDatabaseDump.GetStem();
                        if (dumpStem != null)
                        {
                            TryDumpSideDatabase(_sideDb, targetBtrId, "raw", creationMethod,
                                dumpStem, verbose);
                        }
                        _sideDb.Insunits = sourceUnits;
                        if (_sideDb.PlotStyleMode != sourcePlotStyleMode)
                            throw new InvalidOperationException(Msg.Text("selplot.stb_mismatch"));

                        // 複製直後の図形は側DBのモデル空間 BTR にある。ペーパー空間源は
                        // 側DBのレイアウトへ移し、元レイアウトと同じ座標系・用紙で
                        // 範囲プロットできるようにする
                        if (sourceIsModelSpace)
                        {
                            // 窓・範囲の座標系（DCS）を元図面と一致させる。複製できない場合は
                            // 平面ビュー前提で続行し、その旨を報告する
                            CopyActiveModelView(doc.Database, _sideDb, log);
                        }
                        else
                        {
                            // レイアウトのプロット検証（PlotInfoValidator）は対象レイアウトが
                            // その DB のカレントであることを要求する（eLayoutNotCurrent）
                            MakeLayoutCurrent(_sideDb, targetLayoutId);
                        }
                        if (sourceIsModelSpace)
                            PrepareEntities(_sideDb, targetBtrId);
                        else
                            PreparePaperEntities(_sideDb, paperSelectedIds!);
                        _sideDb.UpdateExt(true);
                        // 側DBの対象レイアウトへ元のページ設定（用紙・尺度・回転・スタイルシート・
                        // 領域）を複製する。PDF デバイスへの上書きは PlotPort が行う
                        ApplySourceSettings(_sideDb, targetLayoutId, sourceSettings);
                        if (dumpStem != null)
                            TryDumpSideDatabase(_sideDb, targetBtrId, "ready", creationMethod,
                                dumpStem, verbose);
                        Plotter = new CadPlotPort(doc, _sideDb, targetLayoutId);
                    }
                    catch
                    {
                        _sideDb?.Dispose();
                        _sideDb = null;
                        throw;
                    }
                }
            }
            finally
            {
                viewportClipIds.Dispose();
                supported.Dispose();
            }
        }

        static CadTransaction BeginPaperViewportIsolation(Database db,
            ObjectIdCollection selected, ObjectIdCollection clipIds,
            out IsolationSnapshot snapshot)
        {
            var keep = new HashSet<ObjectId>();
            foreach (ObjectId id in selected) keep.Add(id);
            foreach (ObjectId id in clipIds) keep.Add(id);
            var changed = new List<IsolationEntityState>();
            var temporaryLayers = new List<ObjectId>();
            var tr = new CadTransaction(db);
            try
            {
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var replacementLayers = new Dictionary<ObjectId, ObjectId>();

                // Viewportと同時選択されたペーパー空間の図形も、他経路と同じく非印刷レイヤから一時退避する。
                // 元レイヤ自体を変更するとViewport内のモデル図形まで変わるため、選択したペーパー空間の図形だけを移す。
                foreach (ObjectId id in selected)
                {
                    var entity = (Entity)tr.GetObject(id, OpenMode.ForRead, false, true);
                    if (entity is Viewport) continue; // Viewport枠は元レイヤの印刷可否に従う
                    var layer = (LayerTableRecord)tr.GetObject(entity.LayerId, OpenMode.ForRead);
                    bool needsLayer = layer.IsOff || layer.IsFrozen || !layer.IsPlottable;
                    if (!needsLayer && entity.Visible) continue;
                    changed.Add(new IsolationEntityState(id, entity.Visible, entity.LayerId));
                    entity.UpgradeOpen();
                    entity.Visible = true;
                    if (!needsLayer) continue;
                    if (!replacementLayers.TryGetValue(layer.ObjectId, out ObjectId replacementId))
                    {
                        string baseName = $"CADPETA_SELECTION_{layer.ObjectId.Handle}";
                        string name = baseName;
                        for (int suffix = 1; lt.Has(name); suffix++) name = $"{baseName}_{suffix}";
                        replacementId = AddPlotEnabledLayer(tr, lt, name, layer).ObjectId;
                        replacementLayers[layer.ObjectId] = replacementId;
                        temporaryLayers.Add(replacementId);
                    }
                    entity.LayerId = replacementId;
                }

                foreach (ObjectId id in space)
                {
                    var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    bool controller = entity is Viewport viewport &&
                        SelectionViewportPolicy.IsController(viewport.Number,
                            ProductPluginDefaults.PaperSpaceViewportNumber);
                    if (controller || keep.Contains(id) || !entity.Visible) continue;
                    changed.Add(new IsolationEntityState(id, entity.Visible, entity.LayerId));
                    entity = (Entity)tr.GetObject(id, OpenMode.ForWrite, false, true);
                    entity.Visible = false;
                }
                // commitせず保持する。プロット完了後のDisposeで全変更をabortし、元図面を戻す。
                snapshot = new IsolationSnapshot(changed.ToArray(), temporaryLayers.ToArray());
                return tr;
            }
            catch
            {
                tr.Dispose();
                throw;
            }
        }

        static void VerifyPaperViewportIsolationRestored(Database db,
            IsolationSnapshot snapshot)
        {
            using var tr = new CadTransaction(db);
            foreach (var state in snapshot.Entities)
            {
                if (!state.Id.IsValid || state.Id.IsErased ||
                    tr.GetObject(state.Id, OpenMode.ForRead) is not Entity entity ||
                    entity.Visible != state.Visible || entity.LayerId != state.LayerId)
                    throw new InvalidOperationException(
                        $"Viewport選択出力後に元図面の図形状態が戻っていない: {state.Id}");
            }
            foreach (ObjectId id in snapshot.TemporaryLayers)
                if (id.IsValid && !id.IsErased)
                    throw new InvalidOperationException(
                        $"Viewport選択出力後に一時レイヤが残っている: {id}");
        }

        readonly struct IsolationEntityState
        {
            public ObjectId Id { get; }
            public bool Visible { get; }
            public ObjectId LayerId { get; }
            public IsolationEntityState(ObjectId id, bool visible, ObjectId layerId) =>
                (Id, Visible, LayerId) = (id, visible, layerId);
        }

        sealed class IsolationSnapshot
        {
            public IReadOnlyList<IsolationEntityState> Entities { get; }
            public IReadOnlyList<ObjectId> TemporaryLayers { get; }
            public IsolationSnapshot(IReadOnlyList<IsolationEntityState> entities,
                IReadOnlyList<ObjectId> temporaryLayers) =>
                (Entities, TemporaryLayers) = (entities, temporaryLayers);
        }

        /// <summary>
        /// 元の画層の色・線の太さ・線種・印刷スタイルを写した、印刷する画層を画層テーブルに足して返す
        /// （非表示・凍結・印刷しない画層の図形を、印刷のあいだだけ移す先）。
        /// </summary>
        static LayerTableRecord AddPlotEnabledLayer(CadTransaction tr, LayerTable lt, string name, LayerTableRecord source)
        {
            lt.UpgradeOpen();
            var record = NewPlotEnabledLayerRecord(name, source);
            lt.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        static LayerTableRecord NewPlotEnabledLayerRecord(string name,
            LayerTableRecord source)
        {
            var replacement = new LayerTableRecord
            {
                Name = name,
                Color = source.Color,
                LineWeight = source.LineWeight,
                LinetypeObjectId = source.LinetypeObjectId,
                IsPlottable = true,
            };
            // CTB図面等ではPlotStyleNameIdがNullになり得る。Nullをsetterへ渡すとeNullObjectId。
            if (!source.PlotStyleNameId.IsNull)
                replacement.PlotStyleNameId = source.PlotStyleNameId;
            return replacement;
        }

        /// <summary>
        /// ペーパー空間図形はowner/layout/graphics contextを変えずに複製する。Viewportは表示内容を
        /// 所有しないためモデル空間を含むDB全体を複製し、対象レイアウトから未選択図形だけを除く。
        /// </summary>
        static Database CreatePaperSideDatabase(Database source, string sourceLayoutName,
            ObjectIdCollection selected,
            out ObjectId targetLayoutId, out ObjectId targetBtrId,
            out IReadOnlyList<ObjectId> targetSelectedIds)
        {
            Database side = source.Wblock();
            try
            {
                (targetLayoutId, targetBtrId) = FindPaperLayout(side, sourceLayoutName);
                MakeLayoutCurrent(side, targetLayoutId);

                var paperId = targetBtrId; // out 引数はラムダの中で使えないので写す
                InWorkingDatabase(side, tr =>
                {
                    var paper = (BlockTableRecord)tr.GetObject(paperId, OpenMode.ForRead);
                    foreach (ObjectId id in paper)
                    {
                        var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
                        if (entity is Viewport viewport &&
                            SelectionViewportPolicy.IsController(viewport.Number,
                                ProductPluginDefaults.PaperSpaceViewportNumber)) continue;
                        entity.UpgradeOpen();
                        entity.Erase();
                    }
                    tr.Commit();
                });

                var paperIds = new ObjectIdCollection();
                foreach (ObjectId id in selected)
                    paperIds.Add(id);
                IReadOnlyDictionary<ObjectId, ObjectId> mapped;
                try
                {
                    mapped = ClonePaperObjectsWithIdentityCheck(source, side, paperIds,
                        targetBtrId);
                }
                finally
                {
                    paperIds.Dispose();
                }
                var selectedTargets = selected.Cast<ObjectId>().Select(id => mapped[id]).ToArray();
                targetSelectedIds = selectedTargets;
                return side;
            }
            catch
            {
                side.Dispose();
                throw;
            }
        }


        static IReadOnlyDictionary<ObjectId, ObjectId> ClonePaperObjectsWithIdentityCheck(
            Database source, Database side, ObjectIdCollection sourceIds,
            ObjectId targetOwnerId)
        {
            var sourceTypes = new Dictionary<ObjectId, string>();
            using (var tr = new CadTransaction(source))
            {
                foreach (ObjectId id in sourceIds)
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead) as Entity
                        ?? throw new InvalidOperationException($"ペーパー空間の元IDがEntityではない: {id}");
                    sourceTypes[id] = entity.GetType().Name;
                }
            }

            using var map = new IdMapping();
            source.WblockCloneObjects(sourceIds, targetOwnerId, map,
                DuplicateRecordCloning.Replace, false);
            var result = new Dictionary<ObjectId, ObjectId>();
            var targets = new HashSet<ObjectId>();
            foreach (ObjectId sourceId in sourceIds)
            {
                if (!map.Contains(sourceId))
                    throw new InvalidOperationException(
                        $"ペーパー空間複製のID対応がない: {sourceId.Handle}");
                IdPair pair = map[sourceId];
                if (!pair.IsCloned || !pair.IsPrimary || pair.Value.IsNull ||
                    !pair.Value.IsValid || pair.Value.IsErased || !targets.Add(pair.Value))
                    throw new InvalidOperationException(
                        $"ペーパー空間複製のID対応が不正: {sourceId.Handle}");
                result[sourceId] = pair.Value;
            }

            InWorkingDatabase(side, tr =>
            {
                foreach (var pair in result)
                {
                    var entity = tr.GetObject(pair.Value, OpenMode.ForRead) as Entity
                        ?? throw new InvalidOperationException(
                            $"ペーパー空間複製後のIDがEntityではない: {pair.Key.Handle}");
                    if (entity.OwnerId != targetOwnerId ||
                        !string.Equals(entity.GetType().Name, sourceTypes[pair.Key],
                            StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"ペーパー空間複製後のownerまたは型が一致しない: {pair.Key.Handle}");
                }
            });
            return result;
        }

        // 試験用の故障注入。1 のとき Wblock を使わず複製フォールバック経路を実走させる（tests/tools/README.md）。
        const string ForceCloneFallbackEnvVar = "CADPETA_FORCE_SELECTION_CLONE_FALLBACK";

        static Database CreateSideDatabase(Database source, ObjectIdCollection supported,
            SideDatabaseMetadata sourceSnapshot, TextWriter log, out string creationMethod)
        {
            Database? side = null;
            if (Environment.GetEnvironmentVariable(ForceCloneFallbackEnvVar) == "1")
            {
                log.WriteLine(Msg.Format("selplot.force_clone", ForceCloneFallbackEnvVar));
            }
            else
            {
                try
                {
                    side = source.Wblock(supported, Point3d.Origin);
                    if (ModelSpaceHasEntities(side))
                    {
                        var (_, modelId) = GetModelLayout(side);
                        var candidate = CaptureSideDatabaseMetadata(side, modelId,
                            "candidate", "Wblock");
                        var mismatch = SideDatabaseSnapshotComparer.Compare(sourceSnapshot, candidate);
                        if (mismatch == SideDatabaseSnapshotMismatch.None)
                        {
                            creationMethod = "Wblock";
                            return side;
                        }
                        if (mismatch == SideDatabaseSnapshotMismatch.Extents &&
                            TryNormalizeUniformTranslation(side, modelId, sourceSnapshot, candidate,
                                out var displacement))
                        {
                            creationMethod = "Wblock+translation";
                            log.WriteLine(Msg.Format("selplot.translation_normalized", displacement));
                            return side;
                        }
                        log.WriteLine(Msg.Format("selplot.wblock_mismatch",
                            SideDatabaseSnapshotComparer.Describe(sourceSnapshot, candidate)));
                        side.Dispose();
                        side = null;
                    }
                    else
                    {
                        log.WriteLine(Msg.Text("selplot.wblock_empty"));
                        side.Dispose();
                        side = null;
                    }
                }
                catch (CadRuntimeException ex)
                {
                    side?.Dispose();
                    log.WriteLine(Msg.Format("selplot.wblock_failed", ex));
                }
            }

            side = new Database(true, true);
            try
            {
                ObjectId modelId;
                using (var tr = new CadTransaction(side))
                {
                    var bt = (BlockTable)tr.GetObject(side.BlockTableId, OpenMode.ForRead);
                    modelId = bt[BlockTableRecord.ModelSpace];
                    tr.Commit();
                }
                using (var idMap = new IdMapping())
                    source.WblockCloneObjects(supported, modelId, idMap,
                        DuplicateRecordCloning.Replace, false);
                if (!ModelSpaceHasEntities(side))
                    throw new InvalidOperationException("オブジェクト複製後の側DBモデル空間が空");
                var cloneSnapshot = CaptureSideDatabaseMetadata(side, modelId,
                    "candidate", "clone");
                var cloneMismatch = SideDatabaseSnapshotComparer.Compare(sourceSnapshot, cloneSnapshot);
                if (cloneMismatch == SideDatabaseSnapshotMismatch.Extents &&
                    TryNormalizeUniformTranslation(side, modelId, sourceSnapshot, cloneSnapshot,
                        out var displacement))
                {
                    creationMethod = "clone+translation";
                    log.WriteLine(Msg.Format("selplot.translation_normalized", displacement));
                    return side;
                }
                if (cloneMismatch != SideDatabaseSnapshotMismatch.None)
                    throw new InvalidOperationException(Msg.Format("selplot.clone_mismatch",
                        SideDatabaseSnapshotComparer.Describe(sourceSnapshot, cloneSnapshot)));
                creationMethod = "clone";
                return side;
            }
            catch
            {
                side.Dispose();
                throw;
            }
        }

        static bool TryNormalizeUniformTranslation(Database side, ObjectId modelId,
            SideDatabaseMetadata sourceSnapshot, SideDatabaseMetadata candidate,
            out string displacementText)
        {
            displacementText = "";
            if (!SideDatabaseSnapshotComparer.TryGetUniformTranslation(
                    sourceSnapshot, candidate, out double[] displacement))
                return false;

            var vector = new Vector3d(displacement[0], displacement[1], displacement[2]);
            InWorkingDatabase(side, tr =>
            {
                var model = (BlockTableRecord)tr.GetObject(modelId, OpenMode.ForRead);
                var matrix = Matrix3d.Displacement(vector);
                foreach (ObjectId id in model)
                {
                    var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                    entity.TransformBy(matrix);
                }
                tr.Commit();
            });
            side.UpdateExt(true);

            var normalized = CaptureSideDatabaseMetadata(side, modelId,
                "candidate", candidate.CreationMethod);
            if (SideDatabaseSnapshotComparer.Compare(sourceSnapshot, normalized) !=
                SideDatabaseSnapshotMismatch.None)
                return false;
            displacementText = FormattableString.Invariant(
                $"({displacement[0]:R}, {displacement[1]:R}, {displacement[2]:R})");
            return true;
        }

        static void TryDumpSideDatabase(Database db, ObjectId btrId, string observation,
            string creationMethod, string stem, TextWriter verbose)
        {
            try
            {
                SideDatabaseDump.Write(stem,
                    CaptureSideDatabaseMetadata(db, btrId, observation, creationMethod));
                verbose.WriteLine($"側DB metadata を保存: {SideDatabaseDump.GetPath(stem, observation)}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // 計測フックは製品処理の成否を変えない。失敗理由は verbose trace へ必ず残す。
                verbose.WriteLine($"側DB metadata の保存に失敗 ({observation}): {ex.Message}");
            }
        }

        static SideDatabaseMetadata CaptureSelectionMetadata(Database db,
            ObjectIdCollection ids)
        {
            var inventory = new EntityInventory();
            using (var tr = new CadTransaction(db))
            {
                foreach (ObjectId id in ids)
                    inventory.Add(tr, id);
            }
            return new SideDatabaseMetadata
            {
                Observation = "source",
                CreationMethod = "source",
                EntityCount = inventory.Count,
                EntityTypes = inventory.Types,
                ExtentsUnavailableCount = inventory.ExtentsUnavailable,
                Extents = inventory.Extents,
            };
        }

        static SideDatabaseMetadata CaptureSideDatabaseMetadata(Database db, ObjectId btrId,
            string observation, string creationMethod)
        {
            var inventory = new EntityInventory();
            var view = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            var layoutValues = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            var plotValues = new SortedDictionary<string, string?>(StringComparer.Ordinal);

            using (var tr = new CadTransaction(db))
            {
                var space = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                    inventory.Add(tr, id);
                var layout = (Layout)tr.GetObject(space.LayoutId, OpenMode.ForRead);
                CaptureProperties(layout, layoutValues, "LayoutName", "ModelType", "TabOrder");
                CaptureProperties(layout, plotValues,
                    "PlotType", "PlotRotation", "PlotCentered", "PlotHidden", "PlotTransparency",
                    "UseStandardScale", "StandardScale", "CustomPrintScale", "PlotPaperUnits",
                    "PlotOrigin", "PlotWindowArea", "CanonicalMediaName", "PlotConfigurationName",
                    "CurrentStyleSheet");

                try
                {
                    var vt = (ViewportTable)tr.GetObject(db.ViewportTableId, OpenMode.ForRead);
                    const string activeName = "*Active";
                    if (vt.Has(activeName))
                    {
                        var active = tr.GetObject(vt[activeName], OpenMode.ForRead);
                        CaptureProperties(active, view, "ViewDirection", "ViewTwist", "Target",
                            "CenterPoint", "Height", "Width");
                    }
                }
                catch (CadRuntimeException ex)
                {
                    view["Unavailable"] = ex.ErrorStatus.ToString();
                }
            }

            return new SideDatabaseMetadata
            {
                Observation = observation,
                CreationMethod = creationMethod,
                EntityCount = inventory.Count,
                EntityTypes = inventory.Types,
                ExtentsUnavailableCount = inventory.ExtentsUnavailable,
                Extents = inventory.Extents,
                View = view,
                Layout = layoutValues,
                PlotSettings = plotValues,
            };
        }

        /// <summary>
        /// 図形の種類ごとの数と、範囲を取れた図形の範囲の和（元の選択と側DB の計測の記録で共通）。
        /// 範囲を取れない図形・読めない図形も数え、種類は "NonEntity"・"Unavailable" として残す。
        /// </summary>
        sealed class EntityInventory
        {
            public SortedDictionary<string, int> Types { get; } = new(StringComparer.Ordinal);
            public int Count { get; private set; }
            public int ExtentsUnavailable { get; private set; }
            bool _hasExtents;
            double _minX, _minY, _minZ, _maxX, _maxY, _maxZ;

            public SideDatabaseExtents? Extents => _hasExtents
                ? new SideDatabaseExtents { Min = new[] { _minX, _minY, _minZ }, Max = new[] { _maxX, _maxY, _maxZ } }
                : null;

            void CountType(string type) => Types[type] = Types.TryGetValue(type, out int old) ? old + 1 : 1;

            public void Add(CadTransaction tr, ObjectId id)
            {
                Count++;
                Entity? entity = null;
                try
                {
                    entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    CountType(entity?.GetType().Name ?? "NonEntity");
                    if (entity == null || !TryGetStableExtents(entity, out Extents3d ext))
                    {
                        ExtentsUnavailable++;
                        return;
                    }
                    if (!_hasExtents)
                    {
                        _minX = ext.MinPoint.X; _minY = ext.MinPoint.Y; _minZ = ext.MinPoint.Z;
                        _maxX = ext.MaxPoint.X; _maxY = ext.MaxPoint.Y; _maxZ = ext.MaxPoint.Z;
                        _hasExtents = true;
                    }
                    else
                    {
                        _minX = Math.Min(_minX, ext.MinPoint.X); _minY = Math.Min(_minY, ext.MinPoint.Y);
                        _minZ = Math.Min(_minZ, ext.MinPoint.Z); _maxX = Math.Max(_maxX, ext.MaxPoint.X);
                        _maxY = Math.Max(_maxY, ext.MaxPoint.Y); _maxZ = Math.Max(_maxZ, ext.MaxPoint.Z);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    ExtentsUnavailable++;
                    if (entity == null)
                        CountType("Unavailable");
                }
            }
        }
        static void CaptureProperties(object source, IDictionary<string, string?> target,
            params string[] propertyNames)
        {
            var type = source.GetType();
            foreach (string name in propertyNames)
            {
                try
                {
                    var property = type.GetProperty(name);
                    target[name] = property == null
                        ? null
                        : Convert.ToString(property.GetValue(source, null), CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    target[name] = null;
                }
            }
        }

        // 元の選択と側DB の照合に使う範囲。表示の状態で変わる GeometricExtents を避け、図形の定義の点から求める。
        // ARES 2027 は幅のあるポリラインとスプラインの GeometricExtents を、レイアウトで印刷した後の元図面では
        // 線の幅などを含めて返し、表示していない側DB では含めずに返した（2026-10-25 実測。幅 0.6 のポリラインで
        // 元図面 337.905〜390.066、側DB 338〜390）。照合は一様な平行移動と欠落を見つけるためのものなので、
        // 両方で同じ点から作れば足りる。この 2 種類と OLE 以外は GeometricExtents を使う
        static bool TryGetStableExtents(Entity entity, out Extents3d extents)
        {
            extents = default;
            switch (entity)
            {
                case Ole2Frame ole:
                    var rectangle = ole.Position3d;
                    return TryGetBox(new[]
                    {
                        rectangle.LowerLeft,
                        rectangle.LowerRight,
                        rectangle.UpperLeft,
                        rectangle.UpperRight,
                    }, out extents);
                case Polyline polyline:
                    return TryGetBox(Enumerable.Range(0, polyline.NumberOfVertices)
                        .Select(polyline.GetPoint3dAt), out extents);
                case Spline spline:
                    return TryGetBox(Enumerable.Range(0, spline.NumControlPoints)
                        .Select(spline.GetControlPointAt), out extents);
            }
            extents = entity.GeometricExtents;
            return IsValidExtents(extents);
        }

        static bool TryGetBox(IEnumerable<Point3d> source, out Extents3d extents)
        {
            extents = default;
            var points = source.ToList();
            if (points.Count == 0) return false;
            extents = new Extents3d(
                new Point3d(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
                new Point3d(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));
            return IsValidExtents(extents);
        }

        static bool IsValidExtents(Extents3d extents)
        {
            return Numbers.IsFinite(extents.MinPoint.X) && Numbers.IsFinite(extents.MinPoint.Y) &&
                Numbers.IsFinite(extents.MinPoint.Z) && Numbers.IsFinite(extents.MaxPoint.X) &&
                Numbers.IsFinite(extents.MaxPoint.Y) && Numbers.IsFinite(extents.MaxPoint.Z) &&
                extents.MinPoint.X <= extents.MaxPoint.X &&
                extents.MinPoint.Y <= extents.MaxPoint.Y &&
                extents.MinPoint.Z <= extents.MaxPoint.Z;
        }

        static BlockTableRecord OpenModelSpace(CadTransaction tr, Database db)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        }

        static bool ModelSpaceHasEntities(Database db)
        {
            using var tr = new CadTransaction(db);
            var model = OpenModelSpace(tr, db);
            foreach (ObjectId _ in model) return true;
            return false;
        }

        static (ObjectId LayoutId, ObjectId BtrId) GetModelLayout(Database db)
        {
            using var tr = new CadTransaction(db);
            var model = OpenModelSpace(tr, db);
            return (model.LayoutId, model.ObjectId);
        }

        /// <summary>
        /// <paramref name="db"/> を作業中の図面（HostApplicationServices.WorkingDatabase）にして <paramref name="body"/> を実行し、
        /// 作業中の図面を必ず元へ戻す。LayoutManager や図形の一部の操作は作業中の図面を対象にするため、側DB を扱う間だけ差し替える。
        /// </summary>
        static T WithWorkingDatabase<T>(Database db, Func<T> body)
        {
            Database saved = HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase = db;
                return body();
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = saved;
            }
        }

        /// <summary><paramref name="db"/> を作業中の図面にし、そのトランザクションで <paramref name="body"/> を実行する（確定は body が Commit で行う）。</summary>
        static T InWorkingDatabase<T>(Database db, Func<CadTransaction, T> body) =>
            WithWorkingDatabase(db, () =>
            {
                using var tr = new CadTransaction(db);
                return body(tr);
            });

        static void InWorkingDatabase(Database db, Action<CadTransaction> body) =>
            InWorkingDatabase(db, tr => { body(tr); return true; });

        static (ObjectId LayoutId, ObjectId BtrId) FindPaperLayout(Database db,
            string layoutName)
        {
            var found = InWorkingDatabase(db, tr =>
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (!layout.ModelType && string.Equals(layout.LayoutName, layoutName,
                            StringComparison.Ordinal))
                        return ((ObjectId LayoutId, ObjectId BtrId)?)(layout.ObjectId, layout.BlockTableRecordId);
                }
                return null;
            });
            return found ?? throw new InvalidOperationException($"Wblock後の側DBに元レイアウトがない: {layoutName}");
        }

        /// <summary>
        /// 元図面のモデル空間アクティブビュー（視線方向・ねじれ・注視点）を側DBへ複製し、
        /// 窓プロットの座標系（DCS）と範囲の向きを元図面と一致させる。複製できない場合は
        /// 平面ビュー前提のまま続行するため、対象と理由を報告する。
        /// </summary>
        static void CopyActiveModelView(Database source, Database side, TextWriter log)
        {
            const string activeName = "*Active";
            try
            {
                using var trS = new CadTransaction(source);
                using var trD = new CadTransaction(side);
                var vtS = (ViewportTable)trS.GetObject(source.ViewportTableId, OpenMode.ForRead);
                var vtD = (ViewportTable)trD.GetObject(side.ViewportTableId, OpenMode.ForRead);
                if (!vtS.Has(activeName) || !vtD.Has(activeName))
                {
                    log.WriteLine(Msg.Format("selplot.view_copy_failed", activeName));
                    return;
                }
                var s = (ViewportTableRecord)trS.GetObject(vtS[activeName], OpenMode.ForRead);
                var d = (ViewportTableRecord)trD.GetObject(vtD[activeName], OpenMode.ForWrite);
                d.ViewDirection = s.ViewDirection;
                d.ViewTwist = s.ViewTwist;
                d.Target = s.Target;
                d.CenterPoint = s.CenterPoint;
                d.Height = s.Height;
                d.Width = s.Width;
                trD.Commit();
            }
            catch (CadRuntimeException ex)
            {
                log.WriteLine(Msg.Format("selplot.view_copy_failed", ex.ErrorStatus));
            }
        }

        /// <summary>
        /// 側DBのカレントレイアウトを対象レイアウトへ切り替える（LayoutManager は
        /// WorkingDatabase を対象にするため、切替の間だけ退避して差し替える）。
        /// </summary>
        static void MakeLayoutCurrent(Database db, ObjectId layoutId)
        {
            string layoutName;
            using (var tr = new CadTransaction(db))
            {
                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                layoutName = layout.LayoutName;
            }
            WithWorkingDatabase(db, () => LayoutManager.Current.CurrentLayout = layoutName);
        }

        /// <summary>側DBの対象レイアウトへ元レイアウトのページ設定を複製する。</summary>
        static void ApplySourceSettings(Database db, ObjectId layoutId, PlotSettings sourceSettings)
        {
            using var tr = new CadTransaction(db);
            var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
            layout.CopyFrom(sourceSettings);
            tr.Commit();
        }

        static void PrepareEntities(Database db, ObjectId btrId)
        {
            // Teigha 系ホストは側DBの Entity ラッパー生成時にも WorkingDatabase を参照する。
            // 元図面のままでは側DBの ObjectId を開く際に eWrongDatabase となるため、
            // 側DBの図形を開く区間だけ差し替え、例外時も必ず元へ戻す。
            InWorkingDatabase(db, tr =>
            {
                var space = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                LayerTableRecord? defTarget = null;
                foreach (ObjectId id in space)
                {
                    var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite); ent.Visible = true;
                    var layer = (LayerTableRecord)tr.GetObject(ent.LayerId, OpenMode.ForWrite);
                    if (string.Equals(layer.Name, NonPlotLayerName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (defTarget == null)
                        {
                            if (lt.Has(PlotEnabledLayerName))
                            {
                                // 元図面由来の同名レイヤはオフ・フリーズのまま複製され得るため、印刷可能化と併せて解除する
                                defTarget = (LayerTableRecord)tr.GetObject(
                                    lt[PlotEnabledLayerName], OpenMode.ForWrite);
                                EnablePlotting(defTarget);
                            }
                            else
                            {
                                defTarget = AddPlotEnabledLayer(tr, lt, PlotEnabledLayerName, layer);
                            }
                            ent.LayerId = defTarget.ObjectId;
                        }
                        else ent.LayerId = defTarget.ObjectId;
                    }
                    else EnablePlotting(layer);
                }
                tr.Commit();
            });
        }

        /// <summary>
        /// 画層を表示・凍結解除・印刷可にする。値が違うものだけを書く。
        /// 側DB は Wblock で元図面の現在の画層を引き継ぎ、作業中の図面を側DB に切り替えている間は、
        /// その画層の IsFrozen に書くと値が同じでも AutoCAD が eInvalidLayer を返す。
        /// 現在の画層は凍結できないので、凍結の解除が要るのは現在の画層ではないときだけになる。
        /// </summary>
        static void EnablePlotting(LayerTableRecord layer)
        {
            if (layer.IsOff) layer.IsOff = false;
            if (layer.IsFrozen) layer.IsFrozen = false;
            if (!layer.IsPlottable) layer.IsPlottable = true;
        }

        static void PreparePaperEntities(Database db, IReadOnlyList<ObjectId> selectedIds)
        {
            InWorkingDatabase(db, tr =>
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var replacementLayers = new Dictionary<ObjectId, ObjectId>();
                foreach (ObjectId id in selectedIds)
                {
                    var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite, false, true);
                    entity.Visible = true;
                    var layer = (LayerTableRecord)tr.GetObject(entity.LayerId, OpenMode.ForRead);
                    if (!layer.IsOff && !layer.IsFrozen && layer.IsPlottable) continue;
                    if (!replacementLayers.TryGetValue(layer.ObjectId, out ObjectId replacementId))
                    {
                        replacementId = AddPlotEnabledLayer(tr, lt, $"CADPETA_SELECTION_{layer.ObjectId.Handle}", layer).ObjectId;
                        replacementLayers[layer.ObjectId] = replacementId;
                    }
                    entity.LayerId = replacementId;
                }
                tr.Commit();
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var isolation = _sourceIsolation;
            var snapshot = _isolationSnapshot;
            var sideDb = _sideDb;
            _sourceIsolation = null;
            _isolationSnapshot = null;
            _sideDb = null;
            bool isolationReleased = false;
            CleanupRunner.RunAll(
                () =>
                {
                    if (isolation == null) return;
                    CleanupRunner.RunAll(isolation.Abort, isolation.Dispose);
                    isolationReleased = true;
                },
                () =>
                {
                    if (isolationReleased && snapshot != null)
                        VerifyPaperViewportIsolationRestored(_sourceDb, snapshot);
                },
                () => sideDb?.Dispose(),
                () =>
                {
                    // 側DB を印刷して破棄した直後の、元の図面の最初の印刷だけ、側DB に複製した文字の
                    // TrueType フォント（Arial）が別のフォント（MS ゴシック）で印刷される（AutoCAD 2026 で確認。
                    // 2 回目の印刷と、間で元の図面を再作図したときは起きない）。元の図面を再作図して、
                    // 次の印刷を側DB を使う前と同じにする
                    if (sideDb != null) _sourceDoc.Editor.Regen();
                });
        }
    }
}
