//
// Licensed to the Apache Software Foundation (ASF) under one
// or more contributor license agreements.  See the NOTICE file
// distributed with this work for additional information
// regarding copyright ownership.  The ASF licenses this file
// to you under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License.  You may obtain a copy of the License at
//
//   https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations
// under the License.
//

using System.Collections.Generic;
using org.apache.plc4net.api;
using org.apache.plc4net.api.authentication;
using org.apache.plc4net.exceptions;

namespace org.apache.plc4net
{
    /// <summary>
    /// Registry of PLC protocol drivers. A driver registers itself here;
    /// connection requests are dispatched to the driver whose protocol code
    /// matches the connection string scheme.
    /// </summary>
    public class PlcDriverManager
    {
        private static PlcDriverManager _instance;

        private readonly Dictionary<string, IPlcDriver> _drivers
            = new Dictionary<string, IPlcDriver>();

        private PlcDriverManager()
        {
        }

        public static PlcDriverManager Instance => _instance ?? (_instance = new PlcDriverManager());

        public void RegisterDriver(IPlcDriver driver)
        {
            _drivers[driver.ProtocolCode] = driver;
        }

        public IPlcDriver GetDriver(string connectionString)
        {
            var parsed = ConnectionString.Parse(connectionString);
            return GetDriverByCode(parsed.ProtocolCode);
        }

        public IPlcDriver GetDriverByCode(string protocolCode)
        {
            if (_drivers.TryGetValue(protocolCode.ToLowerInvariant(), out var driver))
            {
                return driver;
            }
            throw new PlcConnectionException(
                $"No driver registered for protocol '{protocolCode}'.");
        }

        public IPlcConnection GetConnection(string connectionString)
        {
            return GetConnection(connectionString, null);
        }

        public IPlcConnection GetConnection(string connectionString, IPlcAuthentication authentication)
        {
            var driver = GetDriver(connectionString);
            return driver.Connect(connectionString, authentication);
        }
    }
}
