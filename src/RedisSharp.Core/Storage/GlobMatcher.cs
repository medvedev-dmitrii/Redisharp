using System.Text;
using System.Text.RegularExpressions;

namespace RedisSharp.Core.Storage;

/// <summary>
/// Compiles Redis-style glob patterns (<c>*</c>, <c>?</c>, <c>[abc]</c>, <c>\x</c>) into predicates.
/// </summary>
public static class GlobMatcher
{
    public static Func<string, bool> Compile(string pattern)
    {
        if (pattern == "*")
            return static _ => true;

        var regex = new Regex("^" + ToRegex(pattern) + "$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        return regex.IsMatch;
    }

    private static string ToRegex(string pattern)
    {
        var sb = new StringBuilder(pattern.Length * 2);
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                case '\\' when i + 1 < pattern.Length:
                    sb.Append(Regex.Escape(pattern[++i].ToString()));
                    break;
                case '[':
                    var close = pattern.IndexOf(']', i + 1);
                    if (close < 0) { sb.Append("\\["); break; }
                    sb.Append('[').Append(pattern, i + 1, close - i - 1).Append(']');
                    i = close;
                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.ToString();
    }
}
