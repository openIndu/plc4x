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
using org.apache.plc4net.api;
using org.apache.plc4net.api.authentication;
using org.apache.plc4net.api.metadata;
using org.apache.plc4net.messages;
using org.apache.plc4net.model;
using org.apache.plc4net.spi.transports;

namespace org.apache.plc4net.spi.drivers
{
    /// <summary>
    /// A connection that wraps a transport instance.
    /// </summary>
    /// <remarks>
    /// Mirrors the Java SPI3 <c>ConnectionBase</c>. Concrete driver connections extend
    /// this and wire a <see cref="MessageCodecBase{TMessage}"/> over the transport.
    /// </remarks>
    public abstract class ConnectionBase : IPlcConnection
    {
        private readonly ITransportInstance _transportInstance;
        private readonly ConnectionString _connectionString;
        private IPlcAuthentication _authentication;

        protected ConnectionBase(ConnectionString connectionString, ITransportInstance transportInstance)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _transportInstance = transportInstance ?? throw new ArgumentNullException(nameof(transportInstance));
        }

        protected ITransportInstance TransportInstance => _transportInstance;

        protected ConnectionString ConnectionInfo => _connectionString;

        public IPlcAuthentication Authentication
        {
            get => _authentication;
            set => _authentication = value;
        }

        public bool IsConnected => _transportInstance.IsOpen;

        public virtual void Close()
        {
            _transportInstance.Close();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }
            try
            {
                Close();
            }
            catch (TransportException)
            {
                // Dispose must not throw.
            }
        }

        public abstract IPlcConnectionMetadata PlcConnectionMetadata { get; }

        public abstract IPlcTag Parse(string tagQuery);

        public abstract IPlcReadRequestBuilder ReadRequestBuilder { get; }

        public abstract IPlcWriteRequestBuilder WriteRequestBuilder { get; }

        public abstract IPlcSubscriptionRequestBuilder SubscriptionRequestBuilder { get; }

        public abstract IPlcUnsubscriptionRequestBuilder UnsubscriptionRequestBuilder { get; }
    }
}
