using CadPeta.Out.Emf;
using Xunit;

namespace CadPeta.Tests;

public sealed class EmfClipboardFailureTests
{
    sealed class Native : IEmfClipboardNative
    {
        public int LastError => 5;
        public string? Fail;
        public Queue<bool> OpenResults { get; } = new();
        public List<IntPtr> Deleted { get; } = new();
        public int CloseCount;
        public byte[] Bytes = Array.Empty<byte>();
        public IntPtr GetEnhMetaFile(string path) => Fail == "Load" ? IntPtr.Zero : new IntPtr(10);
        public IntPtr CopyEnhMetaFile(IntPtr source) => Fail == "Copy" ? IntPtr.Zero : new IntPtr(20);
        public bool DeleteEnhMetaFile(IntPtr handle) { Deleted.Add(handle); return true; }
        public bool OpenClipboard() => OpenResults.Count > 0 ? OpenResults.Dequeue() : Fail != "Open";
        public bool EmptyClipboard() => Fail != "Empty";
        public IntPtr SetClipboardData(IntPtr handle) => Fail == "Set" ? IntPtr.Zero : handle;
        public bool CloseClipboard() { CloseCount++; return Fail != "Close"; }
        public IntPtr GetClipboardData() => Fail == "ReadMissing" ? IntPtr.Zero : new IntPtr(30);
        public uint GetEnhMetaFileBits(IntPtr handle, uint size, byte[]? buffer)
        {
            if (size == 0) return Fail == "SizeZero" ? 0 : (uint)Bytes.Length;
            byte[] source = Fail == "Mismatch" ? Bytes.Select((x, i) => i == 0 ? (byte)(x ^ 1) : x).ToArray() : Bytes;
            Array.Copy(source, buffer!, source.Length);
            return Fail == "Short" ? (uint)(source.Length - 1) : (uint)source.Length;
        }
        public IntPtr GetOpenClipboardWindow() => IntPtr.Zero;
        public uint GetWindowThreadProcessId(IntPtr window, out uint processId) { processId = 0; return 0; }
    }

    [Fact]
    public void Open競合後に成功し_成功したcopyと読戻しhandleを解放しない()
    {
        WithEmf((path, bytes) =>
        {
            var native = new Native { Bytes = bytes };
            native.OpenResults.Enqueue(false); native.OpenResults.Enqueue(false);
            native.OpenResults.Enqueue(true); native.OpenResults.Enqueue(true);
            var sleeps = new List<int>();
            EmfClipboard.SetFromFile(path, TextWriter.Null, TextWriter.Null, native, sleeps.Add, 10, 7);
            Assert.Equal(new[] { 7, 7 }, sleeps);
            Assert.Equal(new[] { new IntPtr(10) }, native.Deleted);
            Assert.Equal(2, native.CloseCount);
        });
    }

    [Fact]
    public void Open競合上限ではClipboardを変更せずcopyを解放する()
    {
        WithEmf((path, bytes) =>
        {
            var native = new Native { Bytes = bytes, Fail = "Open" };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                EmfClipboard.SetFromFile(path, TextWriter.Null, null, native, _ => { }, 3, 0));
            Assert.Contains("3", ex.Message);
            Assert.Equal(new[] { new IntPtr(10), new IntPtr(20) }, native.Deleted);
            Assert.Equal(0, native.CloseCount);
        });
    }

    [Theory]
    [InlineData("Load")]
    [InlineData("Copy")]
    [InlineData("Empty")]
    [InlineData("Set")]
    [InlineData("ReadMissing")]
    [InlineData("SizeZero")]
    [InlineData("Short")]
    [InlineData("Mismatch")]
    public void 各APIと読戻し異常を顕在化し所有権を守る(string failure)
    {
        WithEmf((path, bytes) =>
        {
            var native = new Native { Bytes = bytes, Fail = failure };
            Assert.Throws<InvalidOperationException>(() =>
                EmfClipboard.SetFromFile(path, TextWriter.Null, null, native, _ => { }, 2, 0));
            if (failure != "Load") Assert.Contains(new IntPtr(10), native.Deleted);
            if (failure is "Empty" or "Set") Assert.Contains(new IntPtr(20), native.Deleted);
            if (failure is "ReadMissing" or "SizeZero" or "Short" or "Mismatch")
                Assert.DoesNotContain(new IntPtr(20), native.Deleted); // Set成功後はシステム所有
            Assert.DoesNotContain(new IntPtr(30), native.Deleted); // 読戻しhandleは常にシステム所有
        });
    }

    [Fact]
    public void Close失敗は登録結果を上書きせず警告する()
    {
        WithEmf((path, bytes) =>
        {
            var native = new Native { Bytes = bytes, Fail = "Close" };
            var log = new StringWriter();
            EmfClipboard.SetFromFile(path, log, null, native, _ => { }, 2, 0);
            Assert.Contains("CloseClipboard が失敗", log.ToString());
            Assert.Equal(2, native.CloseCount);
        });
    }

    static void WithEmf(Action<string, byte[]> run)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".emf");
        byte[] bytes = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(path, bytes);
        try { run(path, bytes); } finally { File.Delete(path); }
    }
}
