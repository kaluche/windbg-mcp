using System.Text;
using ClrDebug;
using ClrDebug.DbgEng;

namespace WinDbgMCP.Server.KernelDebug;

/// <summary>
/// Captures WinDbg command output via IDebugOutputCallbacks.
/// Thread-safe buffer for concurrent output from the DbgEng thread.
/// </summary>
public sealed class OutputCapture : IDebugOutputCallbacks
{
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();

    public HRESULT Output(DEBUG_OUTPUT mask, string text)
    {
        lock (_lock)
        {
            _buffer.Append(text);
        }
        return HRESULT.S_OK;
    }

    /// <summary>
    /// Get accumulated output and clear the buffer.
    /// </summary>
    public string GetAndClear()
    {
        lock (_lock)
        {
            var result = _buffer.ToString();
            _buffer.Clear();
            return result;
        }
    }

    /// <summary>
    /// Clear the buffer without returning content.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }
}
