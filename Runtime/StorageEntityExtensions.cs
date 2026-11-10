using System.Text;
using UnityEngine;

namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 表格和图片：以原始文件存储（不加密、不压缩、立即落盘），方便用外部工具打开。file 需带扩展名，如 "rank.csv"、"shot.png"。
    /// </summary>
    public static class StorageEntityExtensions
    {
        // 带 BOM，Excel 才能正确识别 UTF-8 中文
        private static readonly UTF8Encoding CsvEncoding = new(true);

        public static void SaveSheet(this StorageEntity storage, string file, StorageSheet sheet)
        {
            var preamble = CsvEncoding.GetPreamble();
            var body = CsvEncoding.GetBytes(sheet.ToCsv());
            var data = new byte[preamble.Length + body.Length];
            preamble.CopyTo(data, 0);
            body.CopyTo(data, preamble.Length);
            storage.SaveRaw(file, data);
        }

        /// <summary>
        /// 文件不存在返回 null。
        /// </summary>
        public static StorageSheet LoadSheet(this StorageEntity storage, string file)
        {
            var data = storage.LoadRaw(file);
            return data == null ? null : StorageSheet.Parse(CsvEncoding.GetString(data));
        }

        /// <summary>
        /// 扩展名为 .jpg/.jpeg 时存 JPG，否则存 PNG。贴图需开启 Read/Write 且为未压缩格式。
        /// </summary>
        public static void SaveImage(this StorageEntity storage, string file, Texture2D texture, int jpgQuality = 75)
        {
            bool jpg = file.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jpeg", System.StringComparison.OrdinalIgnoreCase);
            storage.SaveRaw(file, jpg ? texture.EncodeToJPG(jpgQuality) : texture.EncodeToPNG());
        }

        /// <summary>
        /// 文件不存在或不是图片时返回 null。返回的贴图由调用方负责 Destroy。
        /// </summary>
        public static Texture2D LoadImage(this StorageEntity storage, string file)
        {
            var data = storage.LoadRaw(file);
            if (data == null)
                return null;

            var texture = new Texture2D(2, 2);
            if (texture.LoadImage(data))
                return texture;

            Object.Destroy(texture);
            return null;
        }
    }
}
