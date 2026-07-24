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
using System.Collections.Generic;
using System.Text.RegularExpressions;
using org.apache.plc4net.exceptions;

namespace org.apache.plc4net.api
{
    /// <summary>
    /// A parsed PLC4X connection string:
    /// <c>{protocol-code}(:{transport-code})?://{transport-config}(?{parameter-string})?</c>
    /// </summary>
    /// <remarks>
    /// The grammar is kept identical to the Java SPI3 <c>DriverBase.URI_PATTERN</c> so the
    /// same connection string addresses the same device from either language. Examples:
    /// <c>s7://192.168.0.1</c>, <c>s7:cotp://10.0.0.5:102?remote-rack=0&amp;remote-slot=1</c>,
    /// <c>modbus-tcp://10.0.0.9:502?unit-identifier=1</c>.
    /// </remarks>
    public sealed class ConnectionString
    {
        private static readonly Regex UriPattern = new Regex(
            @"^(?<protocolCode>[a-z0-9\-]*)(:(?<transportCode>[a-z0-9\-]*))?://(?<transportConfig>[^?]*)(\?(?<paramString>.*))?$",
            RegexOptions.Compiled);

        // Masks the value of any query parameter whose name looks like it carries a
        // credential, so connection strings can be logged safely.
        private static readonly Regex SecretParamPattern = new Regex(
            @"([?&][^=&]*(?:password|passwd|secret|token)[^=&]*=)[^&]*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private ConnectionString(
            string protocolCode,
            string transportCode,
            string transportConfig,
            string paramString,
            IReadOnlyDictionary<string, string> parameters)
        {
            ProtocolCode = protocolCode;
            TransportCode = transportCode;
            TransportConfig = transportConfig;
            ParamString = paramString;
            Parameters = parameters;
        }

        /// <summary>e.g. "s7", "modbus-tcp".</summary>
        public string ProtocolCode { get; }

        /// <summary>e.g. "tcp", "cotp". Null when the string relies on the driver's default.</summary>
        public string TransportCode { get; }

        /// <summary>The address the transport consumes, e.g. "192.168.0.1:102".</summary>
        public string TransportConfig { get; }

        /// <summary>The raw query string, without the leading '?'. Empty when absent.</summary>
        public string ParamString { get; }

        /// <summary>Query parameters, parsed. Keys are case-insensitive.</summary>
        public IReadOnlyDictionary<string, string> Parameters { get; }

        public static ConnectionString Parse(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new PlcConnectionException("Connection string must not be empty.");
            }

            var match = UriPattern.Match(connectionString);
            if (!match.Success)
            {
                throw new PlcConnectionException(
                    "Connection string doesn't match the format " +
                    "'{protocol-code}(:{transport-code})?://{transport-config}(?{parameter-string})?'");
            }

            var protocolCode = match.Groups["protocolCode"].Value;
            if (string.IsNullOrEmpty(protocolCode))
            {
                throw new PlcConnectionException("Connection string is missing the protocol code.");
            }

            var transportGroup = match.Groups["transportCode"];
            var transportCode = transportGroup.Success && transportGroup.Value.Length > 0
                ? transportGroup.Value
                : null;

            var paramString = match.Groups["paramString"].Success
                ? match.Groups["paramString"].Value
                : string.Empty;

            return new ConnectionString(
                protocolCode,
                transportCode,
                match.Groups["transportConfig"].Value,
                paramString,
                ParseParameters(paramString));
        }

        private static IReadOnlyDictionary<string, string> ParseParameters(string paramString)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(paramString))
            {
                return result;
            }

            foreach (var pair in paramString.Split('&'))
            {
                if (pair.Length == 0)
                {
                    continue;
                }
                var separator = pair.IndexOf('=');
                if (separator < 0)
                {
                    // A bare flag, e.g. "?verbose" — treat as present-and-true.
                    result[Uri.UnescapeDataString(pair)] = "true";
                    continue;
                }
                var key = Uri.UnescapeDataString(pair.Substring(0, separator));
                var value = Uri.UnescapeDataString(pair.Substring(separator + 1));
                result[key] = value;
            }
            return result;
        }

        /// <summary>
        /// Looks up a parameter, returning <paramref name="defaultValue"/> when absent.
        /// </summary>
        public string GetParameter(string name, string defaultValue = null)
        {
            return Parameters.TryGetValue(name, out var value) ? value : defaultValue;
        }

        public int GetIntParameter(string name, int defaultValue)
        {
            var raw = GetParameter(name);
            return int.TryParse(raw, out var value) ? value : defaultValue;
        }

        public bool GetBoolParameter(string name, bool defaultValue)
        {
            var raw = GetParameter(name);
            return bool.TryParse(raw, out var value) ? value : defaultValue;
        }

        /// <summary>
        /// Replaces credential-bearing parameter values with '***' so a connection string
        /// can be written to a log.
        /// </summary>
        public static string RedactSecrets(string connectionString)
        {
            if (connectionString == null)
            {
                return null;
            }
            return SecretParamPattern.Replace(connectionString, "$1***");
        }

        public override string ToString()
        {
            var transport = TransportCode == null ? string.Empty : ":" + TransportCode;
            var parameters = ParamString.Length == 0 ? string.Empty : "?" + ParamString;
            return RedactSecrets($"{ProtocolCode}{transport}://{TransportConfig}{parameters}");
        }
    }
}
