// Licensed to the Apache Software Foundation (ASF) under one
// or more contributor license agreements. See the NOTICE file
// distributed with this work for additional information
// regarding copyright ownership. The ASF licenses this file
// to you under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License. You may obtain a copy of the License at
// https://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied. See the License for the
// specific language governing permissions and limitations
// under the License.

using System;
using System.Linq;
using System.Threading.Tasks;
using org.apache.plc4net.drivers.slmp;
using org.apache.plc4net.messages;
using org.apache.plc4net.spi.drivers;
using org.apache.plc4net.spi.drivers.messages;
using org.apache.plc4net.transports.test;
using org.apache.plc4net.types;
using Xunit;

namespace org.apache.plc4net.test.slmp
{
    public class SlmpMultiBlockWriteTests
    {
        [Fact]
        public async Task Word_and_bit_ranges_share_one_multi_block_write()
        {
            using var transport = NewTransport();
            using var connection = NewConnection(transport);
            transport.InjectTestData(Response(0));
            var bits = new bool[16];
            bits[0] = true;
            bits[15] = true;
            var request = connection.WriteRequestBuilder!
                .AddTag("words", "D0[0..1]:WORD", (ushort)0x1111, (ushort)0x2222)
                .AddTag("bits", "M0[0..15]:BOOL", bits.Cast<object>().ToArray())
                .Build();

            var response = Assert.IsType<DefaultPlcWriteResponse>(await request.ExecuteAsync());

            Assert.All(response.TagNames, name => Assert.Equal(PlcResponseCode.Ok, response.GetResponseCode(name)));
            Assert.Equal(Convert.FromHexString(
                "500000FFFF03001A001000061400000101" +
                "000000A80200111122220000009001000180"),
                transport.GetAllWrittenData());
        }

        [Fact]
        public async Task Multi_block_remote_error_applies_to_every_grouped_tag()
        {
            using var transport = NewTransport();
            using var connection = NewConnection(transport);
            transport.InjectTestData(Response(0xC05B));
            var request = connection.WriteRequestBuilder!
                .AddTag("first", "D0[0..1]:WORD", (ushort)1, (ushort)2)
                .AddTag("second", "D10[0..1]:WORD", (ushort)3, (ushort)4)
                .Build();

            var response = Assert.IsType<DefaultPlcWriteResponse>(await request.ExecuteAsync());

            Assert.Equal(PlcResponseCode.RemoteError, response.GetResponseCode("first"));
            Assert.Equal(PlcResponseCode.RemoteError, response.GetResponseCode("second"));
            Assert.Equal(0x1406, CommandAt(transport.GetAllWrittenData(), 0));
        }

        [Fact]
        public async Task Scalar_write_keeps_its_position_between_multi_block_groups()
        {
            using var transport = NewTransport();
            using var connection = NewConnection(transport);
            var frames = Task.Run(() =>
            {
                var firstGroup = transport.WaitForWrittenData(37, 5000);
                transport.InjectTestData(Response(0));
                var scalar = transport.WaitForWrittenData(23, 5000);
                transport.InjectTestData(Response(0));
                var secondGroup = transport.WaitForWrittenData(37, 5000);
                transport.InjectTestData(Response(0));
                return firstGroup.Concat(scalar).Concat(secondGroup).ToArray();
            });
            var request = connection.WriteRequestBuilder!
                .AddTag("first", "D0[0..1]:WORD", (ushort)1, (ushort)2)
                .AddTag("second", "D10[0..1]:WORD", (ushort)3, (ushort)4)
                .AddTag("scalar", "D20:WORD", (ushort)5)
                .AddTag("third", "D30[0..1]:WORD", (ushort)6, (ushort)7)
                .AddTag("fourth", "D40[0..1]:WORD", (ushort)8, (ushort)9)
                .Build();

            var response = Assert.IsType<DefaultPlcWriteResponse>(await request.ExecuteAsync());

            Assert.All(response.TagNames, name => Assert.Equal(PlcResponseCode.Ok, response.GetResponseCode(name)));
            var written = await frames;
            Assert.Equal(0x1406, CommandAt(written, 0));
            Assert.Equal(0x1401, CommandAt(written, 37));
            Assert.Equal(0x1406, CommandAt(written, 60));
        }

        [Fact]
        public async Task More_than_120_blocks_split_into_multi_block_then_batch_write()
        {
            using var transport = NewTransport(2048);
            using var connection = NewConnection(transport);
            var frames = Task.Run(() =>
            {
                var multiBlock = transport.WaitForWrittenData(1217, 5000);
                transport.InjectTestData(Response(0));
                var batch = transport.WaitForWrittenData(25, 5000);
                transport.InjectTestData(Response(0));
                return multiBlock.Concat(batch).ToArray();
            });
            var builder = connection.WriteRequestBuilder!;
            for (var i = 0; i < 121; i++)
                builder.AddTag("range" + i, "D" + (i * 2) + "[0..1]:WORD", (ushort)i, (ushort)(i + 1));

            var response = Assert.IsType<DefaultPlcWriteResponse>(await builder.Build().ExecuteAsync());

            Assert.All(response.TagNames, name => Assert.Equal(PlcResponseCode.Ok, response.GetResponseCode(name)));
            var written = await frames;
            Assert.Equal(0x1406, CommandAt(written, 0));
            Assert.Equal(120, written[15]);
            Assert.Equal(0x1401, CommandAt(written, 1217));
        }

        private static int CommandAt(byte[] frame, int offset)
            => frame[offset + 11] | frame[offset + 12] << 8;

        private static TestTransportInstance NewTransport(int bufferSize = 1024)
            => new TestTransportInstance(new TestTransportConfiguration { ReceiveBufferSize = bufferSize });

        private static SlmpConnection NewConnection(TestTransportInstance transport)
            => new SlmpConnection(ConnectionString.Parse("slmp://localhost"), transport);

        private static byte[] Response(ushort endCode) => new byte[]
        {
            0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00,
            0x02, 0x00, (byte)endCode, (byte)(endCode >> 8)
        };
    }
}
