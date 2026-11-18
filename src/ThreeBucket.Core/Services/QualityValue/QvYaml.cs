using System.Globalization;
using System.Text.Json.Nodes;

namespace ThreeBucket.Core.Services.QualityValue;

/// <summary>
/// 受限 YAML（映射/列表/标量/注释）→ JsonNode。避免为配置引入额外 NuGet 依赖。
/// 不支持锚点、多行字面量和复杂标签。
/// </summary>
public static class QvYaml
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static JsonNode ParseFile(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception ex) when(ex is not ConfigValidationException) { var m=System.Text.RegularExpressions.Regex.Match(ex.Message,@"line (\d+)");throw new ConfigValidationException(path,"$",m.Success?int.Parse(m.Groups[1].Value):1,ex.Message); }
    }

    public static JsonNode Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        JoinFlowLines(lines);
        var literalIndent=-1;
        for(var line=0;line<lines.Length;line++)
        {
            var raw=StripComment(lines[line]);
            if(raw.Contains('\t'))throw new InvalidDataException($"line {line+1}: tabs not supported");
            var trim=raw.Trim();if(trim.Length==0)continue;
            if(literalIndent>=0&&Leading(raw)>literalIndent)continue;
            literalIndent=-1;
            if(System.Text.RegularExpressions.Regex.IsMatch(trim,@":\s*[|>][-+]?$"))literalIndent=Leading(raw);
            if(!trim.StartsWith("- ")&&!trim.Contains(':'))throw new InvalidDataException($"line {line+1}: mapping key requires colon");
        }
        var i = 0;
        var node = ParseBlock(lines, ref i, 0);
        while(i<lines.Length && string.IsNullOrWhiteSpace(StripComment(lines[i])))i++;
        if(i<lines.Length)throw new InvalidDataException($"line {i+1}: unexpected trailing block");
        return node ?? new JsonObject();
    }


    private static void JoinFlowLines(string[] lines)
    {
        for(var i=0;i<lines.Length;i++)
        {
            var start=i;var text=StripComment(lines[i]);int depth=0;char quote='\0';
            void Scan(string s){for(var j=0;j<s.Length;j++){var c=s[j];if(quote!='\0'){if(c==quote&&(j==0||s[j-1]!='\\'))quote='\0';continue;}if(c is '\'' or '"'){quote=c;continue;}if(c is '[' or '{')depth++;else if(c is ']' or '}')depth--;}}
            Scan(text);if(depth<0)throw new InvalidDataException($"line {start+1}: unmatched flow closing bracket");
            while((depth>0||quote!='\0')&&i+1<lines.Length){var next=StripComment(lines[++i]);text+=" "+next.Trim();Scan(next);lines[i]="";}
            if(depth!=0||quote!='\0')throw new InvalidDataException($"line {start+1}: unterminated flow/quoted scalar");
            lines[start]=text;
        }
    }

    private static JsonNode? ParseBlock(string[] lines, ref int i, int indent)
    {
        JsonObject? obj = null;
        JsonArray? arr = null;
        while (i < lines.Length)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(StripComment(raw))) { i++; continue; }
            var ind = Leading(raw);
            if (ind < indent) break;
            var t = StripComment(raw).Trim();
            if (t.StartsWith("- "))
            {
                arr ??= new JsonArray();
                var rest = t[2..].Trim();
                i++;
                if (rest.Length == 0)
                    arr.Add(ParseBlock(lines, ref i, ind + 2) ?? new JsonObject());
                else if (LooksLikeInlineMap(rest))
                    arr.Add(ParseInlineMap(rest));
                else if (rest.Contains(':') && !rest.StartsWith('{') && !IsQuoted(rest))
                {
                    // "- key: value" 单行映射项，后续更深缩进行并入
                    var (k, v) = SplitKv(rest);
                    var o = new JsonObject { [k] = v is null ? new JsonObject() : ParseScalar(v) };
                    if (i < lines.Length && Leading(lines[i]) > ind)
                    {
                        var child = ParseBlock(lines, ref i, ind + 2);
                        if (child is JsonObject co)
                        {
                            foreach (var kv in co) { if(o.ContainsKey(kv.Key))throw new InvalidDataException($"line {i+1}: duplicate key {kv.Key}");o[kv.Key] = kv.Value?.DeepClone(); }
                        }
                    }
                    arr.Add(o);
                }
                else arr.Add(ParseScalar(rest));
                continue;
            }
            if (arr is not null) break;
            obj ??= new JsonObject();
            var (key, val) = SplitKv(t);
            if(key.Length==0)throw new InvalidDataException($"line {i+1}: empty key");
            if(obj.ContainsKey(key))throw new InvalidDataException($"line {i+1}: duplicate key {key}");
            i++;
            if(val is "|" or ">")
            {
                var block=new List<string>();
                while(i<lines.Length&&(string.IsNullOrWhiteSpace(lines[i])||Leading(lines[i])>ind)){block.Add(lines[i].Length>=ind+2?lines[i][(ind+2)..]:"");i++;}
                obj[key]=string.Join(val=="|"?"\n":" ",block)+"\n";
            }
            else if (val is null)
            {
                obj[key] = ParseBlock(lines, ref i, ind + 2) ?? new JsonObject();
            }
            else obj[key] = ParseScalar(val);
        }
        return (JsonNode?)obj ?? arr;
    }

    private static bool LooksLikeInlineMap(string s) => s.StartsWith('{') && s.EndsWith('}');
    private static bool IsQuoted(string s) => (s.StartsWith('"') && s.EndsWith('"')) || (s.StartsWith('\'') && s.EndsWith('\''));

    private static JsonObject ParseInlineMap(string s)
    {
        var o = new JsonObject();
        var body = s.Trim();
        if (body.StartsWith('{')) body = body.Trim('{', '}');
        foreach (var part in SplitTop(body, ','))
        {
            var (k, v) = SplitKv(part.Trim());
            if(o.ContainsKey(k))throw new InvalidDataException("duplicate inline key "+k);
            o[k] = v is null ? JsonValue.Create("") : ParseScalar(v);
        }
        return o;
    }

    private static List<string> SplitTop(string s, char sep)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        var depth = 0; var q = '\0';
        foreach (var ch in s)
        {
            if (q != '\0') { cur.Append(ch); if (ch == q) q = '\0'; continue; }
            if (ch is '"' or '\'') { q = ch; cur.Append(ch); continue; }
            if (ch is '{' or '[') depth++;
            if (ch is '}' or ']') depth--;
            if (ch == sep && depth == 0) { list.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }

    private static (string Key, string? Val) SplitKv(string t)
    {
        var q = false; var quote = '\0';
        for (var i = 0; i < t.Length; i++)
        {
            var ch = t[i];
            if (quote != '\0') { if (ch == quote) quote = '\0'; continue; }
            if (ch is '"' or '\'') { quote = ch; continue; }
            if (ch == ':')
            {
                var key = Unquote(t[..i].Trim());
                var rest = t[(i + 1)..].Trim();
                return (key, rest.Length == 0 ? null : rest);
            }
        }
        return (Unquote(t.Trim()), null);
    }

    private static JsonNode ParseScalar(string s)
    {
        s = s.Trim();
        if((s.StartsWith('[')&&!s.EndsWith(']'))||(s.StartsWith('{')&&!s.EndsWith('}'))||(s.StartsWith('"')&&!s.EndsWith('"'))||(s.StartsWith('\'')&&!s.EndsWith('\'')))throw new InvalidDataException("unterminated scalar: "+s);
        if (s is "null" or "~" or "") return JsonValue.Create((string?)null)!;
        if (s is "true" or "True") return JsonValue.Create(true);
        if (s is "false" or "False") return JsonValue.Create(false);
        if (s.StartsWith('[') && s.EndsWith(']'))
        {
            var a = new JsonArray();
            foreach (var p in SplitTop(s.Trim('[', ']'), ','))
                if (p.Trim().Length > 0) a.Add(ParseScalar(p.Trim()));
            return a;
        }
        if (s.StartsWith('{') && s.EndsWith('}')) return ParseInlineMap(s);
        if (double.TryParse(s, NumberStyles.Float, Inv, out var d)
            && !s.StartsWith('0') || s == "0" || s.StartsWith("0.") || s.StartsWith("-") || s.Contains('.') || s.Contains('e') || s.Contains('E') || !s.StartsWith('0'))
        {
            // keep integers as int-like doubles is fine for our D()/I()
            if (long.TryParse(s, NumberStyles.Integer, Inv, out var li) && !s.Contains('.') && !s.Contains('e') && !s.Contains('E'))
                return JsonValue.Create(li);
            if (double.TryParse(s, NumberStyles.Float, Inv, out d))
                return JsonValue.Create(d);
        }
        return JsonValue.Create(Unquote(s));
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s[1..^1];
        return s;
    }

    private static string StripComment(string raw)
    {
        var q = '\0';
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (q != '\0') { if (ch == q) q = '\0'; continue; }
            if (ch is '"' or '\'') { q = ch; continue; }
            if (ch == '#') return raw[..i];
        }
        return raw;
    }

    private static int Leading(string s)
    {
        var n = 0;
        foreach (var ch in s)
        {
            if (ch == ' ') n++;
            else if (ch == '\t') n += 2;
            else break;
        }
        return n;
    }
}
