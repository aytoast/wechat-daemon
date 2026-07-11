using System;
using System.Text;

namespace WeChatSidekick
{
    /// <summary>
    /// shared JSON encoding utilities.
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// escapes a string for safe embedding inside a JSON value.
        /// </summary>
        public static string Escape(string value)
        {
            if (value == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        int i = (int)c;
                        if (i < 32)
                        {
                            sb.AppendFormat("\\u{0:x4}", i);
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// wraps a string in JSON quotes with escaping.
        /// </summary>
        public static string Encode(string s)
        {
            if (s == null) return "null";
            return "\"" + Escape(s) + "\"";
        }
    }
}
