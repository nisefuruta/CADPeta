// 図面のトランザクションと、そのトランザクションで開いたオブジェクトの寿命を 1 つにまとめる（4製品共有）。
//
// ARES（Teigha）の Transaction は、GetObject が返したラッパーを Commit・Abort・Dispose で解放しない。
// 解放されなかったラッパーはネイティブのオブジェクトへの参照を持ち続け、あとで .NET のファイナライザが解放する。
// そのときに図面（または Wblock で作ったデータベース）が閉じていると、解放の処理が閉じたデータベースを読み、
// CAD がアクセス違反で異常終了する（ARES 2020・2023・2024 で、終了の処理の中で起きた。ARES 2020 の完全なダンプで、
// トランザクションを閉じた後の Layout・BlockTableRecord・Polyline などのラッパーがネイティブのポインタを持つことを確かめた）。
// このクラスは開いたオブジェクトを記録し、Dispose でトランザクションより先に、開いた順の逆に Dispose する。
// 返したオブジェクトは Dispose まで使える（AutoCAD のトランザクションと同じ）。AutoCAD もトランザクションで開いた
// オブジェクトの Dispose を許すので、4製品で同じに使う。
using System;
using System.Collections.Generic;
using System.Linq;
using CadPeta.Acquire.Common;

#if CADPETA_ACAD
using Autodesk.AutoCAD.DatabaseServices;
namespace CadPeta.Acquire.Acad;
#elif CADPETA_ARES
using Teigha.DatabaseServices;
namespace CadPeta.Acquire.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.DatabaseServices;
namespace CadPeta.Acquire.Ijcad;
#elif CADPETA_BRICSCAD
using Teigha.DatabaseServices;
namespace CadPeta.Acquire.Bricscad;
#else
#error CAD product symbol is required
#endif

public sealed class CadTransaction : IDisposable
{
    readonly Transaction _tr;
    readonly List<DBObject> _opened = new();
    // 読み取りで開いたオブジェクト。図形ごとに同じ画層・線種・ブロックを開く呼び出し元（CadIrAcquirer）が、
    // 同じオブジェクトのラッパーを図形の数だけ作って Dispose まで持たないように、2 回目からは同じラッパーを返す
    readonly Dictionary<ObjectId, DBObject> _openedForRead = new();
    bool _disposed;

    public CadTransaction(Database db) => _tr = db.TransactionManager.StartTransaction();

    public DBObject GetObject(ObjectId id, OpenMode mode)
    {
        if (mode != OpenMode.ForRead) return Track(_tr.GetObject(id, mode));
        // 削除済みになったオブジェクトは、トランザクションに開かせて同じ例外を返す
        if (_openedForRead.TryGetValue(id, out var opened) && !opened.IsDisposed && !opened.IsErased) return opened;
        var obj = Track(_tr.GetObject(id, mode));
        _openedForRead[id] = obj;
        return obj;
    }

    public DBObject GetObject(ObjectId id, OpenMode mode, bool openErased, bool forceOpenOnLockedLayer)
        => Track(_tr.GetObject(id, mode, openErased, forceOpenOnLockedLayer));

    /// <summary>新しく作ったオブジェクトをトランザクションに加え、Dispose の対象にも加える。</summary>
    public void AddNewlyCreatedDBObject(DBObject obj, bool add)
    {
        _tr.AddNewlyCreatedDBObject(obj, add);
        Track(obj);
    }

    /// <summary>開いて Dispose まで持っているオブジェクトの数（PETADATA の診断に出す）。</summary>
    public int OpenedCount => _opened.Count;

    public void Commit() => _tr.Commit();

    /// <summary>
    /// 開いたオブジェクトを Dispose してから変更を破棄する。破棄で削除される新しいオブジェクトのラッパーを、
    /// 削除の後に Dispose しないため。
    /// </summary>
    public void Abort() => CleanupRunner.RunAll(TakeOpenedDisposals().Append(_tr.Abort).ToArray());

    /// <summary>
    /// 開いたオブジェクトを開いた順の逆に Dispose してから、トランザクションを閉じる（未コミットの変更は破棄される）。
    /// 1 つが失敗しても残りを Dispose し、失敗は例外で返す。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupRunner.RunAll(TakeOpenedDisposals().Append(_tr.Dispose).ToArray());
    }

    DBObject Track(DBObject obj)
    {
        _opened.Add(obj);
        return obj;
    }

    IEnumerable<Action> TakeOpenedDisposals()
    {
        var disposals = Enumerable.Reverse(_opened).Select(obj => (Action)obj.Dispose).ToArray();
        _opened.Clear();
        _openedForRead.Clear();
        return disposals;
    }
}
