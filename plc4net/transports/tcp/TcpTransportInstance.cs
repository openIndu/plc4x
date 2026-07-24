//
// Licensed to the Apache Software Foundation (ASF) under one
// or more contributor license agreements.  See the NOTICE file
// distributed with this work for additional information
// regarding copyright ownership.  The ASF licenses this file
// to you under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License.  You may obtain a copy of the License at
//
//      https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations
// under the License.
//

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using org.apache.plc4net.spi.transports;

namespace org.apache.plc4net.transports.tcp
{
    /// <summary>
    /// TCP transport: one background read loop per connection feeding a ring buffer.
    /// </summary>
    /// <remarks>
    /// The Java SPI3 transport uses a virtual thread per connection blocked in a socket
    /// read. .NET has no virtual threads, but async I/O gives the same result: the read
    /// loop awaits <c>ReceiveAsync</c> and holds no thread while idle. The observable
    /// contract is the same — the loop fills the ring buffer and then invokes the data
    /// listener.
    /// </remarks>
    public class TcpTransportInstance : BaseTransportInstance, IAsyncTransportInstance
    {
        private static readonly byte[] EmptyBytes = new byte[0];

        private readonly Socket _socket;
        private readonly RingBuffer _ringBuffer;
        private readonly object _readLock = new object();
        private readonly object _writeLock = new object();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly Task _readLoop;

        private int _open = 1;
        private volatile Action _dataListener;
        private volatile Action<Exception> _disconnectListener;

        public TcpTransportInstance(IPEndPoint remoteAddress, TcpTransportConfiguration configuration)
            : base(configuration)
        {
            if (remoteAddress == null)
            {
                throw new ArgumentNullException(nameof(remoteAddress));
            }

            _ringBuffer = new RingBuffer(configuration.ReceiveBufferSize);

            Socket socket = null;
            try
            {
                socket = new Socket(remoteAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                if (!string.IsNullOrEmpty(configuration.LocalAddress))
                {
                    socket.Bind(new IPEndPoint(IPAddress.Parse(configuration.LocalAddress), configuration.LocalPort));
                }

                socket.NoDelay = configuration.TcpNoDelay;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, configuration.KeepAlive);
                if (configuration.SendBufferSize > 0)
                {
                    socket.SendBufferSize = configuration.SendBufferSize;
                }
                if (configuration.ReceiveBufferSize > 0)
                {
                    socket.ReceiveBufferSize = configuration.ReceiveBufferSize;
                }

                Connect(socket, remoteAddress, configuration.ConnectTimeout);

                _socket = socket;
                RemoteAddress = remoteAddress;
                LocalAddress = socket.LocalEndPoint as IPEndPoint;
            }
            catch (Exception e)
            {
                socket?.Dispose();
                throw new TransportException(
                    $"Failed to connect to {remoteAddress.Address}:{remoteAddress.Port} - {e.Message}", e);
            }

            // Started last so a throw during setup cannot leak a running loop.
            _readLoop = Task.Run(RunReadLoopAsync);
        }

        public IPEndPoint RemoteAddress { get; }

        public IPEndPoint LocalAddress { get; }

        /// <summary>
        /// Connects with a bounded wait. ConnectAsync has no timeout overload, so the
        /// timeout is applied by racing it and disposing the socket on expiry — closing
        /// the socket is what aborts an in-flight connect.
        /// </summary>
        private static void Connect(Socket socket, IPEndPoint remoteAddress, int connectTimeoutMillis)
        {
            var connectTask = socket.ConnectAsync(remoteAddress);
            if (!connectTask.Wait(connectTimeoutMillis))
            {
                throw new TimeoutException(
                    $"Connection to {remoteAddress.Address}:{remoteAddress.Port} timed out after {connectTimeoutMillis} ms.");
            }
            // Surface any connect error (Wait already completed, so this cannot block).
            connectTask.GetAwaiter().GetResult();
        }

        public override bool IsOpen => Volatile.Read(ref _open) == 1 && _socket.Connected;

        public override int GetNumBytesAvailable()
        {
            lock (_readLock)
            {
                return IsOpen || _ringBuffer.AvailableForReading > 0
                    ? _ringBuffer.AvailableForReading
                    : 0;
            }
        }

        public override byte[] PeekReadableBytes(int numBytes)
        {
            if (numBytes <= 0)
            {
                return EmptyBytes;
            }
            lock (_readLock)
            {
                if (_ringBuffer.AvailableForReading < numBytes)
                {
                    throw new TransportException(
                        $"Requested {numBytes} bytes but only {_ringBuffer.AvailableForReading} available");
                }
                return _ringBuffer.Peek(numBytes);
            }
        }

        public override byte[] Read(int numBytes)
        {
            if (numBytes <= 0)
            {
                return EmptyBytes;
            }
            lock (_readLock)
            {
                if (_ringBuffer.AvailableForReading < numBytes)
                {
                    throw new TransportException(
                        $"Requested {numBytes} bytes but only {_ringBuffer.AvailableForReading} available");
                }
                return _ringBuffer.Read(numBytes);
            }
        }

        public override void Write(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return;
            }

            lock (_writeLock)
            {
                EnsureOpen();
                try
                {
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        offset += _socket.Send(bytes, offset, bytes.Length - offset, SocketFlags.None);
                    }
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _open) == 0)
                {
                    // A concurrent Close() disposed the socket mid-write: normal shutdown.
                }
                catch (SocketException e)
                {
                    throw new TransportException("Failed to write data", e);
                }
            }
        }

        public override void Close()
        {
            // Run the shutdown exactly once even under concurrent/repeated calls.
            if (Interlocked.CompareExchange(ref _open, 0, 1) != 1)
            {
                return;
            }

            // Deliberately takes no locks: closing the socket is what unblocks the read
            // loop. Taking _writeLock first would deadlock against a blocked writer.
            _shutdown.Cancel();
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // Already torn down by the peer; nothing to do.
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _socket.Dispose();
                // Do not wait on the read loop when Close() is called from inside it
                // (e.g. a disconnect listener closing the connection).
                if (_readLoop != null && !_readLoop.IsCompleted && Task.CurrentId != _readLoop.Id)
                {
                    _readLoop.Wait(TimeSpan.FromSeconds(1));
                }
                _shutdown.Dispose();
            }
        }

        public void RegisterDataListener(Action listener) => _dataListener = listener;

        public void RemoveDataListener() => _dataListener = null;

        public void RegisterDisconnectListener(Action<Exception> listener) => _disconnectListener = listener;

        public void RemoveDisconnectListener() => _disconnectListener = null;

        private void NotifyDisconnect(Exception cause)
        {
            var listener = _disconnectListener;
            if (listener == null)
            {
                return;
            }
            try
            {
                listener(cause);
            }
            catch
            {
                // A misbehaving listener must not take down the read loop.
            }
        }

        private void NotifyData()
        {
            var listener = _dataListener;
            if (listener == null)
            {
                return;
            }
            try
            {
                listener();
            }
            catch
            {
                // Same reasoning as NotifyDisconnect.
            }
        }

        private async Task RunReadLoopAsync()
        {
            var buffer = new byte[8192];
            try
            {
                while (Volatile.Read(ref _open) == 1)
                {
                    int free;
                    lock (_readLock)
                    {
                        free = _ringBuffer.RemainingForWriting;
                    }
                    if (free == 0)
                    {
                        // Backpressure: the codec has not drained yet. Wait and retry
                        // rather than disconnect — only the codec knows frame boundaries.
                        await Task.Delay(1, _shutdown.Token).ConfigureAwait(false);
                        continue;
                    }

                    var toRead = Math.Min(buffer.Length, free);
                    var bytesRead = await _socket
                        .ReceiveAsync(new ArraySegment<byte>(buffer, 0, toRead), SocketFlags.None)
                        .ConfigureAwait(false);

                    if (bytesRead == 0)
                    {
                        // Orderly shutdown by the remote end.
                        Volatile.Write(ref _open, 0);
                        NotifyDisconnect(null);
                        return;
                    }

                    lock (_readLock)
                    {
                        _ringBuffer.Write(buffer, 0, bytesRead);
                    }

                    // Notify outside the lock; the bytes are already buffered.
                    NotifyData();
                }
            }
            catch (OperationCanceledException)
            {
                // Close() cancelled us: normal shutdown.
            }
            catch (ObjectDisposedException)
            {
                // Close() disposed the socket while we were awaiting a receive.
            }
            catch (Exception e)
            {
                // Only a failure while still open counts as a disconnect.
                if (Volatile.Read(ref _open) == 1)
                {
                    Volatile.Write(ref _open, 0);
                    NotifyDisconnect(e);
                }
            }
        }
    }
}
