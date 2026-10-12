using System.Text;
using System.Xml;
using System.Xml.Linq;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Fumilume.Services;

/// <summary>同梱定義にない設定ファイルの構文。値とキーを別の意味色で扱う。</summary>
internal static class AdditionalSyntaxHighlighting
{
    private static readonly Lazy<IHighlightingDefinition> Toml = new(() => Load("TOML", TomlRules));
    private static readonly Lazy<IHighlightingDefinition> Yaml = new(() => Load("YAML", CreateYamlRules()));
    private static readonly Lazy<IHighlightingDefinition> Ini = new(() => Load("INI", IniRules));
    private static readonly Lazy<IHighlightingDefinition> Jsonc = new(() => Load("JSONC", JsoncRules));

    internal static IHighlightingDefinition? GetDefinition(string name) => name.ToUpperInvariant() switch
    {
        "TOML" => Toml.Value,
        "YAML" => Yaml.Value,
        "INI" => Ini.Value,
        "JSONC" => Jsonc.Value,
        _ => null,
    };

    private static IHighlightingDefinition Load(string name, string rules)
    {
        var xml = XDocument.Parse($"<SyntaxDefinition name=\"{name}\" xmlns=\"http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008\">{Colors}{rules}</SyntaxDefinition>");
        // 12.0 の Rule 読込は CDATA を扱わず、要素本文では空白と # を regex の装飾とみなす。
        // 正規表現を Text ノードへ揃え、定義中の空白・# は文字そのものとして照合する。
        foreach (var element in xml.Descendants().Where(element => element.Name.LocalName is "Rule" or "Begin" or "End"))
        {
            element.Value = $"(?-x:{element.Value})";
        }

        using var text = new StringReader(xml.ToString(SaveOptions.DisableFormatting));
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    // 初期色も One Dark と揃え、既存の ApplyTheme が意味名から再配色する。
    private const string Colors = """
        <Color name="Property" foreground="#D19A66" />
        <Color name="Section" foreground="#E06C75" />
        <Color name="String" foreground="#98C379" />
        <Color name="Number" foreground="#D19A66" />
        <Color name="Bool" foreground="#D19A66" />
        <Color name="Null" foreground="#D19A66" />
        <Color name="Date" foreground="#D19A66" />
        <Color name="Comment" foreground="#5C6370" />
        <Color name="Operator" foreground="#ABB2BF" />
        <Color name="Entity" foreground="#56B6C2" />
        """;

    // キー用の span を文字列より先に置き、引用キーも Property にする。
    private const string TomlRules = """"
        <RuleSet>
          <Span color="Section">
            <Begin><![CDATA[^[ \t]*(?:\[\[[ \t]*(?:[A-Za-z0-9_-]+|"(?:\\.|[^"\\])*"|'[^']*')(?:[ \t]*\.[ \t]*(?:[A-Za-z0-9_-]+|"(?:\\.|[^"\\])*"|'[^']*'))*[ \t]*\]\]|\[[ \t]*(?:[A-Za-z0-9_-]+|"(?:\\.|[^"\\])*"|'[^']*')(?:[ \t]*\.[ \t]*(?:[A-Za-z0-9_-]+|"(?:\\.|[^"\\])*"|'[^']*'))*[ \t]*\])[ \t]*(?=#|$)]]></Begin>
            <End><![CDATA[(?=)]]></End>
          </Span>
          <Span color="Property">
            <Begin><![CDATA[(?<=^|[{},])[ \t]*(?:[A-Za-z0-9_-]+|"(?:\\.|[^"\\])*"|'[^']*')(?:\s*\.\s*(?:[A-Za-z0-9_-]+|"(?:\\.|[^"\\])*"|'[^']*'))*(?=\s*=)]]></Begin>
            <End><![CDATA[(?=\s*=)]]></End>
          </Span>
          <Span color="String" multiline="true">
            <Begin>"""</Begin>
            <End><![CDATA["{3,5}]]></End>
            <RuleSet><Span color="String"><Begin><![CDATA[\\.]]></Begin><End><![CDATA[(?=)]]></End></Span></RuleSet>
          </Span>
          <Span color="String" multiline="true"><Begin>'''</Begin><End><![CDATA['{3,5}]]></End></Span>
          <Span color="String">
            <Begin>"</Begin><End>"</End>
            <RuleSet><Span color="String"><Begin><![CDATA[\\.]]></Begin><End><![CDATA[(?=)]]></End></Span></RuleSet>
          </Span>
          <Span color="String"><Begin>'</Begin><End>'</End></Span>
          <Span color="Comment"><Begin>#</Begin></Span>
          <Rule color="Date"><![CDATA[(?<![\w.-])(?:\d{4}-\d{2}-\d{2}(?:[Tt ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[Zz]|[+-]\d{2}:\d{2})?)?|\d{2}:\d{2}:\d{2}(?:\.\d+)?)(?![\w.-])]]></Rule>
          <Rule color="Bool"><![CDATA[\b(?:true|false)\b]]></Rule>
          <Rule color="Number"><![CDATA[(?<![\w.-])[+-]?(?:0x[0-9A-Fa-f](?:_?[0-9A-Fa-f])*|0o[0-7](?:_?[0-7])*|0b[01](?:_?[01])*|(?:0|[1-9](?:_?\d)*)(?:\.\d(?:_?\d)*)?(?:[eE][+-]?\d(?:_?\d)*)?|inf|nan)(?![\w.-])]]></Rule>
          <Rule color="Operator"><![CDATA[[=.,\[\]{}]]]></Rule>
        </RuleSet>
        """";

    private const string IniRules = """
        <RuleSet>
          <Span color="Comment"><Begin><![CDATA[^\s*[;#]]]></Begin></Span>
          <Span color="Section"><Begin><![CDATA[^\s*\[]]></Begin><End><![CDATA[\]]]></End></Span>
          <Rule color="Property"><![CDATA[^\s*[^\s=:#;\[\]][^=:\r\n]*(?=\s*[=:])]]></Rule>
          <Rule color="Operator"><![CDATA[[=:]]]></Rule>
          <Span color="String"><Begin>"</Begin><End>"</End><RuleSet><Span color="String"><Begin><![CDATA[\\.]]></Begin><End><![CDATA[(?=)]]></End></Span></RuleSet></Span>
          <Span color="String"><Begin>'</Begin><End>'</End></Span>
          <Rule color="Bool"><![CDATA[\b(?i:true|false|yes|no|on|off)\b]]></Rule>
          <Rule color="Number"><![CDATA[(?<![\w.])[+-]?(?:0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?)(?![\w.])]]></Rule>
        </RuleSet>
        """;

    private const string JsoncRules = """
        <RuleSet>
          <Span color="Property"><Begin><![CDATA["(?:\\.|[^"\\])*"(?=(?:[ \t]|/\*(?:[^*]|\*(?!/))*\*/)*:)]]></Begin><End><![CDATA[(?=)]]></End></Span>
          <Span color="String"><Begin>"</Begin><End>"</End><RuleSet><Span color="String"><Begin><![CDATA[\\.]]></Begin><End><![CDATA[(?=)]]></End></Span></RuleSet></Span>
          <Span color="Comment"><Begin>//</Begin></Span>
          <Span color="Comment" multiline="true"><Begin>/\*</Begin><End>\*/</End></Span>
          <Rule color="Bool"><![CDATA[\b(?:true|false)\b]]></Rule>
          <Rule color="Null"><![CDATA[\bnull\b]]></Rule>
          <Rule color="Number"><![CDATA[(?<![\w.])-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?(?![\w.])]]></Rule>
          <Rule color="Operator"><![CDATA[[{}\[\],:]]]></Rule>
        </RuleSet>
        """;

    private static string CreateYamlRules()
    {
        var rules = new StringBuilder("<RuleSet>");
        // エンジンは行単位で regex を照合するため、開始行の字下げを終端へ動的に渡せない。
        // 通常の設定ファイルで使う 0〜32 桁を別 span にし、同じ深さへの dedent で終える。
        // これより深いブロックは span を開始せず、後続キーへの文字列色の漏れを避ける。
        for (var indent = 0; indent <= 32; indent++)
        {
            for (var sequence = 0; sequence <= 2; sequence++)
            {
                var prefix = sequence switch
                {
                    0 => "^ {INDENT}(?=\\S)(?!-[ \\t])(?:(?:[^#\"'\\r\\n]|\"(?:\\\\.|[^\"\\\\])*\"|'(?:''|[^'])*')*?:[ \\t]+)?",
                    1 => "^ {INDENT}-[ \\t]+",
                    _ => "^ {INDENT}-[ \\t]+(?:[^#\"'\\r\\n]|\"(?:\\\\.|[^\"\\\\])*\"|'(?:''|[^'])*')*?:[ \\t]+",
                };
                rules.Append("""
                    <Span color="String" multiline="true">
                      <Begin><![CDATA[[|>](?:[1-9][+-]?|[+-][1-9]?)?(?=[ \t]*(?:#.*)?$)(?<!^ {33}[^\r\n]*)(?<=PREFIX[|>](?:[1-9][+-]?|[+-][1-9]?)?)]]></Begin>
                      <End><![CDATA[^(?= {0,ENDINDENT}\S)]]></End>
                      <RuleSet><Span color="Comment"><Begin><![CDATA[(?<=PREFIX[|>](?:[1-9][+-]?|[+-][1-9]?)?[ \t]*)#]]></Begin></Span></RuleSet>
                    </Span>
                    """.Replace("PREFIX", prefix, StringComparison.Ordinal)
                    .Replace("ENDINDENT", (indent + (sequence == 2 ? 2 : 0)).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("INDENT", indent.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            }
        }

        rules.Append("""
              <Span color="Property"><Begin><![CDATA[(?:"(?:\\.|[^"\\])*"|'(?:''|[^'])*')(?=\s*:(?:\s|$|[\[\]{},]))]]></Begin><End><![CDATA[(?=\s*:)]]></End></Span>
              <Span color="String" multiline="true"><Begin><![CDATA[(?<=^[ \t]*(?:-[ \t]+)?|:[ \t]+|[\[{,][ \t]*)"]]></Begin><End>"</End><RuleSet><Span color="String"><Begin><![CDATA[\\.]]></Begin><End><![CDATA[(?=)]]></End></Span></RuleSet></Span>
              <Span color="String" multiline="true"><Begin><![CDATA[(?<=^[ \t]*(?:-[ \t]+)?|:[ \t]+|[\[{,][ \t]*)']]></Begin><End>'</End><RuleSet><Span color="String"><Begin>''</Begin><End><![CDATA[(?=)]]></End></Span></RuleSet></Span>
              <Span color="Comment"><Begin><![CDATA[(?<!\S)#]]></Begin></Span>
              <Rule color="Property"><![CDATA[(?:^\s*(?:-\s+)?|(?<=[{,])\s*)(?:[^\s\[\]{},#:'"][^\[\]{},#:\r\n]*?)(?=\s*:(?:\s|$|[\[\]{},]))]]></Rule>
              <Rule color="Entity"><![CDATA[(?<!\S)[&*!][^\s\[\]{},]+]]></Rule>
              <Rule color="Date"><![CDATA[(?<![\w.-])\d{4}-\d{2}-\d{2}(?:[Tt ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[ \t]*(?:[Zz]|[+-]\d{1,2}(?::\d{2})?))?)?(?![\w.-])]]></Rule>
              <Rule color="Bool"><![CDATA[\b(?:true|True|TRUE|false|False|FALSE)\b]]></Rule>
              <Rule color="Null"><![CDATA[\b(?:null|Null|NULL)\b|(?<!\S)~(?!\S)]]></Rule>
              <Rule color="Number"><![CDATA[(?<![\w.-])[+-]?(?:0x[0-9a-fA-F]+|0o[0-7]+|\d+(?:\.\d*)?(?:[eE][+-]?\d+)?|\.\d+(?:[eE][+-]?\d+)?|\.(?:inf|Inf|INF|nan|NaN|NAN))(?![\w.-])]]></Rule>
              <Rule color="Operator"><![CDATA[^\s*(?:---|\.\.\.)(?=\s|$)|[\[\]{},]|:(?=\s|$)|(?<!\S)[?\-](?=\s|$)|[|>][+-]?[1-9]?]]></Rule>
            </RuleSet>
            """);
        return rules.ToString();
    }
}
