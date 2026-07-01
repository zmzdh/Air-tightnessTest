using System;
using System.Threading.Tasks;

namespace AudioActuatorCanTest.Services
{
    public interface IPLCService
    {
        const byte DefaultUnitId = 0x01;

        Task<bool> ConnectAsync(string ipAddress, int port, byte unitId = DefaultUnitId);
        Task DisconnectAsync();
        bool IsConnected { get; }

        Task<bool[]> ReadBitsAsync(string startAddress, ushort count);
        Task<short[]> ReadWordsAsync(string startAddress, ushort count);
        Task WriteBitAsync(string address, bool value);
        Task WriteWordAsync(string address, ushort value);
        Task WriteWordsAsync(string startAddress, byte[] data);

        event EventHandler<string>? OnError;
        event EventHandler<bool>? OnConnectionChanged;
    }

    /// <summary>
    /// 占位实现，表明应用已移除 PLC 依赖。所有读写操作均为无副作用的空实现。
    /// </summary>
    public class PLCService : IPLCService, IDisposable
    {
        private static PLCService? _instance;
        private bool _disposed;

        public static PLCService Instance => _instance ??= new PLCService();

        public static PLCService? Current => _instance;

        private PLCService()
        {
        }

        public event EventHandler<string>? OnError;
        public event EventHandler<bool>? OnConnectionChanged;

        public bool IsConnected => false;

        public Task<bool> ConnectAsync(string ipAddress, int port, byte unitId = IPLCService.DefaultUnitId)
        {
            ThrowIfDisposed();
            OnError?.Invoke(this, "已移除 PLC 连接能力，忽略连接请求。");
            OnConnectionChanged?.Invoke(this, false);
            return Task.FromResult(false);
        }

        public Task DisconnectAsync()
        {
            ThrowIfDisposed();
            OnConnectionChanged?.Invoke(this, false);
            return Task.CompletedTask;
        }

        public Task<bool[]> ReadBitsAsync(string startAddress, ushort count)
        {
            ThrowIfDisposed();
            return Task.FromResult(new bool[count]);
        }

        public Task<short[]> ReadWordsAsync(string startAddress, ushort count)
        {
            ThrowIfDisposed();
            return Task.FromResult(new short[count]);
        }

        public Task WriteBitAsync(string address, bool value)
        {
            ThrowIfDisposed();
            return Task.CompletedTask;
        }

        public Task WriteWordAsync(string address, ushort value)
        {
            ThrowIfDisposed();
            return Task.CompletedTask;
        }

        public Task WriteWordsAsync(string startAddress, byte[] data)
        {
            ThrowIfDisposed();
            return Task.CompletedTask;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PLCService));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            OnConnectionChanged = null;
            OnError = null;
        }
    }
}
