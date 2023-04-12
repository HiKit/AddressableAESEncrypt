using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;


namespace UnityEngine.ResourceManagement.ResourceProviders
{
    public class SeekableAesStream : Stream
    {
        public static string Password = "password";
        public static byte[] Salt = new byte[16] { 0x01, 0x02, 0x01, 0x05, 0x10, 0xAA, 0xBB, 0xCC, 0xDD, 0xF1, 0xF2, 0xF3, 0xF4, 0xF4, 0xE5, 0xE6 };

        private Stream baseStream;
        private AesManaged aes;
        private ICryptoTransform encryptor;
        public bool autoDisposeBaseStream { get; set; } = true;

        /// <param name="salt"></param>
        public SeekableAesStream(Stream baseStream)
        {
            string password = Password;
            byte[] salt = Salt;

            this.baseStream = baseStream;
            using (var key = new PasswordDeriveBytes(password, salt))
            {
                aes = new AesManaged();
                aes.KeySize = 128;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.Key = key.GetBytes(aes.KeySize / 8);
                aes.IV = new byte[16]; //zero buffer is adequate since we have to use new salt for each stream
                encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            }
        }

        private void cipher(byte[] buffer, int offset, int count, long streamPos)
        {
            //find block number
            var blockSizeInByte = aes.BlockSize / 8;
            var blockNumber = (streamPos / blockSizeInByte) + 1;
            var keyPos = streamPos % blockSizeInByte;

            //buffer
            var outBuffer = new byte[blockSizeInByte];
            var nonce = new byte[blockSizeInByte];
            var init = false;

            for (int i = offset; i < count; i++)
            {
                //encrypt the nonce to form next xor buffer (unique key)
                if (!init || (keyPos % blockSizeInByte) == 0)
                {
                    BitConverter.GetBytes(blockNumber).CopyTo(nonce, 0);
                    encryptor.TransformBlock(nonce, 0, nonce.Length, outBuffer, 0);
                    if (init) keyPos = 0;
                    init = true;
                    blockNumber++;
                }
                buffer[i] ^= outBuffer[keyPos]; //simple XOR with generated unique key
                keyPos++;
            }
        }

        public override bool CanRead { get { return baseStream.CanRead; } }
        public override bool CanSeek { get { return baseStream.CanSeek; } }
        public override bool CanWrite { get { return baseStream.CanWrite; } }
        public override long Length { get { return baseStream.Length; } }
        public override long Position { get { return baseStream.Position; } set { baseStream.Position = value; } }
        public override void Flush() { baseStream.Flush(); }
        public override void SetLength(long value) { baseStream.SetLength(value); }
        public override long Seek(long offset, SeekOrigin origin) { return baseStream.Seek(offset, origin); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var streamPos = Position;
            var ret = baseStream.Read(buffer, offset, count);
            cipher(buffer, offset, count, streamPos);
            return ret;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            cipher(buffer, offset, count, Position);
            baseStream.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                encryptor?.Dispose();
                aes?.Dispose();
                if (autoDisposeBaseStream)
                    baseStream?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

