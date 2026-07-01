using System;
using System.Text;

namespace AudioActuatorCanTest.Services
{
    internal static class CodeScanService
    {
        /// <summary>
        /// 标准化扫码结果，移除不可见控制字符和空白字符，返回适合业务存储与展示的产品条码。
        /// </summary>
        /// <param name="raw">扫码枪返回的原始文本。</param>
        /// <returns>清洗后的条码字符串，如果输入为空则返回 <see cref="string.Empty"/>。</returns>
        public static string SanitizeBarcode(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(raw.Length);

            foreach (char ch in raw)
            {
                if (char.IsControl(ch) || char.IsWhiteSpace(ch))
                {
                    continue;
                }

                builder.Append(ch);
            }

            return builder.Length == 0 ? string.Empty : builder.ToString();
        }
    }
}
