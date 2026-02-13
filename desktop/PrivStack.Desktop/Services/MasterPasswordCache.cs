using System.Runtime.InteropServices;
using PrivStack.Desktop.Services.Abstractions;

namespace PrivStack.Desktop.Services;

/// <summary>
/// In-memory cache for the master password, stored in a pinned char[] to
/// prevent the GC from copying it around the heap.
/// </summary>
public sealed class MasterPasswordCache : IMasterPasswordCache, IDisposable
{
    private char[]? _buffer;
    private GCHandle _handle;
    private int _length;
    private bool _disposed;

    public bool HasCachedPassword => _buffer != null && _length > 0;

    public void Set(string password)
    {
        Clear();

        _buffer = new char[password.Length];
        _handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        password.CopyTo(0, _buffer, 0, password.Length);
        _length = password.Length;
    }

    public string? Get()
    {
        if (_buffer == null || _length == 0)
            return null;

        return new string(_buffer, 0, _length);
    }

    public void Clear()
    {
        if (_buffer != null)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            if (_handle.IsAllocated)
                _handle.Free();
            _buffer = null;
            _length = 0;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Clear();
            _disposed = true;
        }
    }
}
