using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dories.Storage.Runtime
{
    /// <summary>
    /// 表格（CSV），可用 Excel 打开。单元格按文本存，读写时与基础类型/枚举互转（不随系统区域变化）。
    /// 通过 StorageEntity 的 SaveSheet / LoadSheet 扩展方法存取。
    /// </summary>
    public sealed class StorageSheet
    {
        private readonly List<List<string>> _rows = new();

        public int RowCount => _rows.Count;
        public int ColumnCount { get; private set; }

        public void SetCell<T>(int column, int row, T value)
        {
            if (column < 0 || row < 0)
                throw new ArgumentOutOfRangeException(column < 0 ? nameof(column) : nameof(row));

            while (_rows.Count <= row)
                _rows.Add(new List<string>());
            var cells = _rows[row];
            while (cells.Count <= column)
                cells.Add(string.Empty);

            cells[column] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            ColumnCount = Math.Max(ColumnCount, column + 1);
        }

        /// <summary>
        /// 单元格不存在或为空时返回 defaultValue；内容无法转成 T 时抛异常。
        /// </summary>
        public T GetCell<T>(int column, int row, T defaultValue = default)
        {
            if (row < 0 || row >= _rows.Count || column < 0 || column >= _rows[row].Count || _rows[row][column].Length == 0)
                return defaultValue;

            var text = _rows[row][column];
            return typeof(T).IsEnum
                ? (T)Enum.Parse(typeof(T), text)
                : (T)Convert.ChangeType(text, typeof(T), CultureInfo.InvariantCulture);
        }

        public string ToCsv()
        {
            var builder = new StringBuilder();
            foreach (var cells in _rows)
            {
                for (int i = 0; i < cells.Count; i++)
                {
                    if (i > 0)
                        builder.Append(',');

                    var cell = cells[i];
                    if (cell.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                        builder.Append('"').Append(cell.Replace("\"", "\"\"")).Append('"');
                    else
                        builder.Append(cell);
                }

                builder.Append('\n');
            }

            return builder.ToString();
        }

        public static StorageSheet Parse(string csv)
        {
            var sheet = new StorageSheet();
            var cell = new StringBuilder();
            int column = 0, row = 0;
            bool quoted = false, rowHasContent = false;

            void EndCell()
            {
                sheet.SetCell(column++, row, cell.ToString());
                cell.Clear();
            }

            // 跳过 UTF-8 BOM
            for (int i = csv.Length > 0 && csv[0] == '﻿' ? 1 : 0; i < csv.Length; i++)
            {
                char c = csv[i];
                if (quoted)
                {
                    if (c != '"')
                        cell.Append(c);
                    else if (i + 1 < csv.Length && csv[i + 1] == '"')
                        cell.Append(csv[++i]); // "" 表示一个引号
                    else
                        quoted = false;
                }
                else if (c == '"')
                {
                    quoted = rowHasContent = true;
                }
                else if (c == ',')
                {
                    EndCell();
                    rowHasContent = true;
                }
                else if (c == '\n')
                {
                    EndCell();
                    row++;
                    column = 0;
                    rowHasContent = false;
                }
                else if (c != '\r')
                {
                    cell.Append(c);
                    rowHasContent = true;
                }
            }

            // 最后一行没有换行符结尾
            if (rowHasContent)
                EndCell();
            return sheet;
        }
    }
}
