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

namespace org.apache.plc4net.spi.transports
{
    /// <summary>
    /// Fixed-capacity byte ring buffer sitting between a transport's read loop and the
    /// protocol codec that drains it.
    /// </summary>
    /// <remarks>
    /// The codec needs to inspect a message header before it knows how long the message
    /// is, so this supports <see cref="Peek"/> (look without consuming) alongside
    /// <see cref="Read"/>. Callers are expected to hold the transport's read lock; this
    /// type is not internally synchronised.
    /// </remarks>
    public class RingBuffer
    {
        private readonly byte[] _buffer;
        private int _readPosition;
        private int _writePosition;
        private int _count;

        public RingBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
            }
            _buffer = new byte[capacity];
        }

        public int Capacity => _buffer.Length;

        public int AvailableForReading => _count;

        public int RemainingForWriting => _buffer.Length - _count;

        public void Write(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            Write(data, 0, data.Length);
        }

        public void Write(byte[] data, int offset, int length)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if (offset < 0 || length < 0 || offset + length > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
            if (length > RemainingForWriting)
            {
                throw new TransportException(
                    $"Ring buffer overflow: tried to write {length} bytes, {RemainingForWriting} free.");
            }

            for (var i = 0; i < length; i++)
            {
                _buffer[_writePosition] = data[offset + i];
                _writePosition = (_writePosition + 1) % _buffer.Length;
            }
            _count += length;
        }

        /// <summary>Returns the next <paramref name="numBytes"/> bytes without consuming them.</summary>
        public byte[] Peek(int numBytes)
        {
            return Copy(numBytes, consume: false);
        }

        /// <summary>Returns the next <paramref name="numBytes"/> bytes and consumes them.</summary>
        public byte[] Read(int numBytes)
        {
            return Copy(numBytes, consume: true);
        }

        private byte[] Copy(int numBytes, bool consume)
        {
            if (numBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(numBytes));
            }
            if (numBytes > _count)
            {
                throw new TransportException(
                    $"Requested {numBytes} bytes but only {_count} available.");
            }

            var result = new byte[numBytes];
            var position = _readPosition;
            for (var i = 0; i < numBytes; i++)
            {
                result[i] = _buffer[position];
                position = (position + 1) % _buffer.Length;
            }

            if (consume)
            {
                _readPosition = position;
                _count -= numBytes;
            }
            return result;
        }

        public void Clear()
        {
            _readPosition = 0;
            _writePosition = 0;
            _count = 0;
        }
    }
}
