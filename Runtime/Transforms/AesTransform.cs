using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Dories.Storage.Runtime
{
    /// <summary>
    /// AES-256-CBC 加密 + HMAC-SHA256 校验，格式：IV(16) + 密文 + HMAC(32)。被篡改或密码不对时 Decode 抛异常。
    /// 密码随包体分发，只能防普通玩家改档，防不了逆向；重要数值仍需服务端校验。
    /// </summary>
    [Serializable]
    public class AesTransform : IStorageTransform
    {
        private const int IvSize = 16;
        private const int MacSize = 32;

        [SerializeField, Tooltip("加密密码。修改后旧存档将无法读取")]
        private string _password = "change-me";

        private byte[] _key;

        public AesTransform() { }

        public AesTransform(string password) => _password = password;

        // shortcut: 密钥直接取 SHA256(密码)，没有加盐慢哈希；密码改为玩家输入的弱口令时换成 PBKDF2
        private byte[] Key
        {
            get
            {
                if (_key == null)
                {
                    using var sha = SHA256.Create();
                    _key = sha.ComputeHash(Encoding.UTF8.GetBytes(_password ?? string.Empty));
                }

                return _key;
            }
        }

        public byte[] Encode(byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.GenerateIV();
            using var encryptor = aes.CreateEncryptor();
            var cipher = encryptor.TransformFinalBlock(data, 0, data.Length);

            var result = new byte[IvSize + cipher.Length + MacSize];
            Buffer.BlockCopy(aes.IV, 0, result, 0, IvSize);
            Buffer.BlockCopy(cipher, 0, result, IvSize, cipher.Length);
            using var hmac = new HMACSHA256(Key);
            Buffer.BlockCopy(hmac.ComputeHash(result, 0, IvSize + cipher.Length), 0, result, IvSize + cipher.Length, MacSize);
            return result;
        }

        public byte[] Decode(byte[] data)
        {
            int cipherLength = data.Length - IvSize - MacSize;
            if (cipherLength <= 0)
                throw new CryptographicException("加密数据长度非法");

            using var hmac = new HMACSHA256(Key);
            var mac = hmac.ComputeHash(data, 0, IvSize + cipherLength);
            int diff = 0;
            for (int i = 0; i < MacSize; i++)
                diff |= mac[i] ^ data[IvSize + cipherLength + i];
            if (diff != 0)
                throw new CryptographicException("校验失败：数据被篡改或密码不正确");

            using var aes = Aes.Create();
            aes.Key = Key;
            var iv = new byte[IvSize];
            Buffer.BlockCopy(data, 0, iv, 0, IvSize);
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(data, IvSize, cipherLength);
        }
    }
}
