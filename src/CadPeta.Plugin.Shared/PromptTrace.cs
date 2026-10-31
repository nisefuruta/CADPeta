using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Threading;
using CadPeta.UseCases;

namespace CadPeta.Plugin.Shared;

/// <summary>CAD のプロンプト生値を試験時だけ JSON Lines へ記録する。</summary>
public static class PromptTrace
{
    public const string PathEnvironmentVariable = "CADPETA_PROMPT_TRACE";
    public const string RunIdEnvironmentVariable = "CADPETA_PROMPT_TRACE_RUN_ID";
    public const string CaseIdEnvironmentVariable = "CADPETA_PROMPT_TRACE_CASE_ID";

    static readonly object WriteLock = new();
    static readonly AsyncLocal<string?> Command = new();
    /// <summary>コマンド種別を現在の実行文脈へ設定し、破棄時に終了行を記録する。</summary>
    public static IDisposable BeginCommand(string command)
    {
        var previous = Command.Value;
        Command.Value = command;
        return new CommandScope(command, previous);
    }

    /// <summary>試験の記録が有効か（環境変数がある）。記録のためだけに CAD の値を読む処理は、有効なときだけ行う。</summary>
    public static bool IsEnabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PathEnvironmentVariable));

    /// <summary>SDK から得た生値を記録する。環境変数未設定時はファイル API を呼ばない。</summary>
    public static void Write(string operation, string status, string? stringResult,
                             bool valueIsNull, int? count)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
            TryWriteCore(path, operation, status, stringResult, valueIsNull, count,
                (file, line) => File.AppendAllText(file, line + Environment.NewLine, TextEncodings.Utf8NoBom));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 環境変数の読取自体が拒否された場合も、製品コマンドは中断しない。
        }
    }

    /// <summary>
    /// コマンドラインへ出した 1 行を記録する。ARES などは COM でコマンドの履歴を読めないため、
    /// E2E が中止の理由を確かめるのに使う。
    /// </summary>
    public static void WriteMessage(string line) =>
        Write("Message", "Line", line, valueIsNull: false, count: null);

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
        var json = JsonText.Serialize(record, indented: false);
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

    // 1 行の JSON の項目。名前と順番は試験のスクリプト（Get-JsonLines で読む）との約束
    [DataContract]
    sealed class PromptTraceRecord
    {
        [DataMember(Name = "runId", Order = 1)] public string? RunId { get; set; }
        [DataMember(Name = "caseId", Order = 2)] public string? CaseId { get; set; }
        [DataMember(Name = "pid", Order = 3)] public int Pid { get; set; }
        [DataMember(Name = "command", Order = 4)] public string? Command { get; set; }
        [DataMember(Name = "operation", Order = 5)] public string Operation { get; set; } = string.Empty;
        [DataMember(Name = "status", Order = 6)] public string Status { get; set; } = string.Empty;
        [DataMember(Name = "stringResult", Order = 7)] public string? StringResult { get; set; }
        [DataMember(Name = "valueIsNull", Order = 8)] public bool ValueIsNull { get; set; }
        [DataMember(Name = "count", Order = 9)] public int? Count { get; set; }
    }
}
