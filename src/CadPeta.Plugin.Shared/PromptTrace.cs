using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CadPeta.Plugin.Shared;

/// <summary>CAD のプロンプト生値を試験時だけ JSON Lines へ記録する。</summary>
public static class PromptTrace
{
    public const string PathEnvironmentVariable = "CADPETA_PROMPT_TRACE";
    public const string RunIdEnvironmentVariable = "CADPETA_PROMPT_TRACE_RUN_ID";
    public const string CaseIdEnvironmentVariable = "CADPETA_PROMPT_TRACE_CASE_ID";

    static readonly object WriteLock = new();
    static readonly AsyncLocal<string?> Command = new();
    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>コマンド種別を現在の実行文脈へ設定し、破棄時に終了行を記録する。</summary>
    public static IDisposable BeginCommand(string command)
    {
        var previous = Command.Value;
        Command.Value = command;
        return new CommandScope(command, previous);
    }

    /// <summary>SDK から得た生値を記録する。環境変数未設定時はファイル API を呼ばない。</summary>
    public static void Write(string operation, string status, string? stringResult,
                             bool valueIsNull, int? count)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
            TryWriteCore(path, operation, status, stringResult, valueIsNull, count,
                (file, line) => File.AppendAllText(file, line + Environment.NewLine, Utf8NoBom));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 環境変数の読取自体が拒否された場合も、製品コマンドは中断しない。
        }
    }

    /// <summary>計測フックの失敗を本処理へ逆流させない。低層WriteCoreは試験可能なthrow契約を保つ。</summary>
    internal static void TryWriteCore(string? path, string operation, string status,
                                      string? stringResult, bool valueIsNull, int? count,
                                      Action<string, string> appendLine)
    {
        try
        {
            WriteCore(path, operation, status, stringResult, valueIsNull, count, appendLine);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // E2Eはtrace欠落を失敗にする。製品コマンドだけは計測I/O失敗で中断しない。
        }
    }

    internal static void WriteCore(string? path, string operation, string status,
                                   string? stringResult, bool valueIsNull, int? count,
                                   Action<string, string> appendLine)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        string tracePath = path!;

        var record = new PromptTraceRecord
        {
            RunId = Environment.GetEnvironmentVariable(RunIdEnvironmentVariable),
            CaseId = Environment.GetEnvironmentVariable(CaseIdEnvironmentVariable),
            Pid = CurrentPid(),
            Command = Command.Value,
            Operation = operation,
            Status = status,
            StringResult = stringResult,
            ValueIsNull = valueIsNull,
            Count = count,
        };
        var json = JsonSerializer.Serialize(record, JsonOptions);
        lock (WriteLock)
            appendLine(tracePath, json);
    }

    static int CurrentPid()
    {
        using var process = Process.GetCurrentProcess();
        return process.Id;
    }

    sealed class CommandScope : IDisposable
    {
        readonly string _command;
        readonly string? _previous;
        bool _disposed;

        public CommandScope(string command, string? previous)
        {
            _command = command;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                Write("Command.End", "End", null, valueIsNull: true, count: null);
            }
            finally
            {
                Command.Value = _previous;
            }
        }
    }

    sealed class PromptTraceRecord
    {
        public string? RunId { get; set; }
        public string? CaseId { get; set; }
        public int Pid { get; set; }
        public string? Command { get; set; }
        public string Operation { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? StringResult { get; set; }
        public bool ValueIsNull { get; set; }
        public int? Count { get; set; }
    }
}
