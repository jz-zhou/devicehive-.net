﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeviceHive.Binary
{
    internal class MessageReaderWriter
    {
        #region Constants

        private static readonly byte[] _messageSignature = new byte[] {0xC5, 0xC3};

        private const int _headerSize = 8;

        #endregion


        private readonly IBinaryConnection _connection;
        private readonly object _lock = new object();

        public MessageReaderWriter(IBinaryConnection connection)
        {
            _connection = connection;
        }


        public Message ReadMessage()
        {
            lock (_lock)
            {
                try
                {
                    var headerBytes = ReadHeaderBytes();

                    byte version;
                    byte flags;
                    ushort dataLength;
                    ushort intent;

                    using (var stream = new MemoryStream(headerBytes))
                    using (var reader = new BinaryReader(stream))
                    {
                        reader.ReadBytes(_messageSignature.Length); // skip signature
                        version = reader.ReadByte();
                        flags = reader.ReadByte();
                        dataLength = reader.ReadUInt16();
                        intent = reader.ReadUInt16();
                    }

                    var data = _connection.Read(dataLength);

                    var checksum = _connection.Read(1)[0];
                    if (checksum != CalculateChecksum(headerBytes.Concat(data)))
                        throw new InvalidOperationException("Invalid message checksum");

                    return new Message(version, flags, intent, data);
                }
                catch (TimeoutException)
                {
                    return null;
                }
            }
        }

        public void WriteMessage(Message message)
        {
            var messageLength = message.Data.Length + _headerSize;

            using (var memoryStream = new MemoryStream(messageLength))
            using (var writer = new BinaryWriter(memoryStream))
            {
                writer.Write(_messageSignature);
                writer.Write(message.Version);
                writer.Write(message.Flags);
                writer.Write((ushort) message.Data.Length);
                writer.Write(message.Intent);
                writer.Write(message.Data);

                var data = memoryStream.ToArray();
                var checksum = CalculateChecksum(data);

                lock (_lock)
                {
                    _connection.Write(data);
                    _connection.Write(new[] {checksum});
                }
            }
        }


        /// <summary>
        /// Read header bytes from underlying connection. If header doesn't start with correct
        /// signature then these bytes will be skipped until correct signature will be found.
        /// </summary>
        /// <returns>Array of header bytes</returns>
        private byte[] ReadHeaderBytes()
        {
            var bytes = _connection.Read(_headerSize);

            // if message is unavailable then IBinaryConnection.Read should throw exception (by timeout)
            while (true)
            {
                var signatureStart = FindSignatureStart(bytes);
                if (signatureStart == 0)
                    return bytes;

                var remainingByteCount = (signatureStart == -1) ? _headerSize : signatureStart;
                var remainingBytes = _connection.Read(remainingByteCount);
                bytes = bytes.Skip(remainingByteCount).Concat(remainingBytes).ToArray();
            }
        }

        /// <summary>
        /// Find index of signature (0xC5, 0xC3) start in the given <paramref name="bytes"/>.
        /// Returns -1 if signture can not be found in <paramref name="bytes"/>.
        /// If signature starts (possibly) in the last byte (only 0xC5 is found) returns index of it.
        /// </summary>
        private static int FindSignatureStart(byte[] bytes)
        {
            var index = -1;
            while (true)
            {
                index = Array.IndexOf(bytes, _messageSignature[0], index + 1);
                if (index == -1)
                    return -1;

                if (index == (_headerSize - 1))
                    return index;

                if (bytes[index + 1] == _messageSignature[1])
                    return index;
            }
        }

        private static byte CalculateChecksum(IEnumerable<byte> bytes)
        {
            return (byte) (0xFF - bytes.Aggregate((b1, b2) => unchecked((byte) (b1 + b2))));
        }
    }
}