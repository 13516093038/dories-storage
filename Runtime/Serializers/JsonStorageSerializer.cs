using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 基于 Newtonsoft.Json：支持基础类型、自定义类、集合、字典，以及 Vector3/Quaternion/Color 等 Unity 结构体。
    /// GameObject/组件引用存成对象上 <see cref="StorageId"/> 的 id，读取时按 id 找回场景里的对象（没挂 StorageId 的存为 null）。
    /// 不写类型名（TypeNameHandling.None），多态字段需要自己加 JsonConverter。
    /// </summary>
    [Serializable]
    public class JsonStorageSerializer : IStorageSerializer
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            ContractResolver = new UnityStructResolver(),
            Converters = { new SceneReferenceConverter() },
        };

        public byte[] Serialize<T>(T value) => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, typeof(T), Settings));

        public T Deserialize<T>(byte[] data) => JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(data), Settings);

        /// <summary>
        /// Unity 结构体的公开属性会互相引用（Vector3.normalized 等），只按字段序列化（含 Vector2Int 这类私有字段）
        /// </summary>
        private sealed class UnityStructResolver : DefaultContractResolver
        {
            /// <summary>
            /// 判断是否为unity结构体
            /// </summary>
            /// <param name="type"></param>
            /// <returns></returns>
            private static bool IsUnityStruct(Type type) =>
                type.IsValueType && !type.IsEnum && !type.IsPrimitive && type.Namespace == "UnityEngine";

            protected override List<MemberInfo> GetSerializableMembers(Type type) =>
                IsUnityStruct(type)
                    ? new List<MemberInfo>(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    : base.GetSerializableMembers(type);

            // Fields 模式下私有字段才可读写
            protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization) =>
                base.CreateProperties(type, IsUnityStruct(type) ? MemberSerialization.Fields : memberSerialization);
        }

        private sealed class SceneReferenceConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => type == typeof(GameObject) || typeof(Component).IsAssignableFrom(type);

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                // 已销毁的对象是 Unity 的"假 null"
                var target = (UnityEngine.Object)value;
                var gameObject = target == null ? null : target is Component component ? component.gameObject : (GameObject)target;
                if (gameObject != null && gameObject.TryGetComponent<StorageId>(out var storageId) && !string.IsNullOrEmpty(storageId.Id))
                {
                    writer.WriteValue(storageId.Id);
                    return;
                }

                if (gameObject != null)
                    Dories.Logging.Log.Warn("Storage", $"{gameObject.name} 没有 StorageId，对它的引用存为 null", gameObject);
                writer.WriteNull();
            }

            public override object ReadJson(JsonReader reader, Type type, object existingValue, JsonSerializer serializer)
            {
                var storageId = reader.TokenType == JsonToken.String ? StorageId.Find((string)reader.Value) : null;
                if (storageId == null)
                    return null;

                return type == typeof(GameObject) ? storageId.gameObject : storageId.GetComponent(type);
            }
        }
    }
}
