using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Dynamic;

using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeTypeResolvers;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.RepresentationModel;


public class UidInfo
{
    public string FilePath { get; set; }
    public int LineNum { get; set; }
}

public enum NodeKind
{
    [JsonPropertyName("H")]
    H,
    [JsonPropertyName("UL")]
    UL,
}

public class Node
{
    public Node(NodeKind kind)
    {
        Kind = kind;
        TemporaryVariables = new Dictionary<string, object>();
    }

    //public string SourceFiles { get; set; } // txt2json 自身がソースファイルを更新するので、最後に取得
    [JsonPropertyOrder(0x00)]
    public NodeKind Kind { get; set; }
    public int? Level { get; set; } = null;
    [JsonPropertyOrder(0x40)]
    public string Id { get; set; }   // sourceFiles の last modified date を基に生成した id を埋め込む
    [JsonPropertyOrder(0x40)]
    public string Text { get; set; } = "";
    [JsonPropertyOrder(0xf0)]
    public Dictionary<string, object> Variables { get; set; } = new Dictionary<string, object>();
    [JsonPropertyOrder(0xff)]
    public List<Node> Children { get; set; } = new List<Node>();

    // JSON シリアライズ時に無視
    [JsonIgnore]
    public Node Parent { get; set; }
    [JsonIgnore]
    public Dictionary<string, object> TemporaryVariables { get; set; } = new Dictionary<string, object>();

}

public class RootNode : Node
{
    public RootNode() : base(NodeKind.H)
    {
        Level = 0;

        // UID重複チェック用
        // 「複数人で１つのファイルを作成（ID自動生成）してマージしたら衝突」は考慮しなくて良いぐらいの確率だけど、「IDごとコピペして重複」が高頻度で発生する恐れがあるので
        TemporaryVariables["uidList"] = new Dictionary<string, LineObject>();
    }
}

#if false
public class TableHeader
{
    public string Name { get; set; }
    public int Id { get; set; }
}
#endif

public class TableHeaderNonInputArea
{
    [JsonIgnore]
    public char Marker { get; set; }

    public int Group { get; set; }
    public string Name { get; set; }
    public string Comment { get; set; }
    public int Size { get; set; }
}

public class HeaderNode : Node
{
    public HeaderNode() : base(NodeKind.H)
    {
        Level = 1;
    }

    //public List<TableHeader> TableHeaders { get; set; } = new List<TableHeader>();
    [JsonPropertyOrder(0x80)]
    public List<TableHeaderNonInputArea> TableHeadersNonInputArea { get; set; } = new List<TableHeaderNonInputArea>();

    [JsonPropertyOrder(0x80)]
    public string Url { get; set; }

    [JsonPropertyOrder(0xf8)]
    public string SrcHash { get; set; }
}

public class ItemNode : Node
{
    public ItemNode() : base(NodeKind.UL)
    {
    }

    [JsonIgnore]
    public int Indent { get; set; }
    [JsonIgnore]
    public string Marker { get; set; }

    [JsonPropertyOrder(0x40)]
    public int Group { get; set; }
    [JsonPropertyOrder(0x40)]
    public int DepthInGroup { get; set; }
    //public List<string> TableData { get; set; }
    [JsonPropertyOrder(0x80)]
    public string Comment { get; set; }
    [JsonPropertyOrder(0x80)]
    public string ImageFilePath { get; set; }
    [JsonPropertyOrder(0x80)]
    public Dictionary<string, object> InitialValues { get; set; }
    [JsonPropertyOrder(0x80)]
    public Dictionary<string, string> Attributes { get; set; }
    [JsonPropertyOrder(0x80)]
    public string Url { get; set; }
}

public class NoIdNodeData
{
    public Node Node { get; set; }
    public LineObject LineObj { get; set; }
    public int LineNum { get; set; }
    public string NewSrcText { get; set; }
}

public class ParseError : Exception
{
    public ParseError(string message, LineObject lineObj) : base($"{message} (File: {lineObj.FilePath}, Line: {lineObj.LineNumber})") { }
}

public class Parser
{
    Stack<Node> Stack { get; set; }
    RootNode Root { get; set; }
    Dictionary<string, List<NoIdNodeData>> NoIdNodes { get; set; } = new Dictionary<string, List<NoIdNodeData>>();

    PathHelper pathHelper;

    readonly V8ScriptEngine _engine;

    public Parser(string rootDirectory)
    {
        pathHelper = new PathHelper(rootDirectory);

        _engine = new V8ScriptEngine();
        LoadLodash();
    }

    private void LoadLodash()
    {
        var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var lodashPath = Path.Combine(exeDirectory, "scripts", "lodash.min.js");
        var lodashScript = File.ReadAllText(lodashPath);
        _engine.Execute(lodashScript);
    }

    public Node ParseLines(IDictionary<string, object> conf, List<LineObject> lineObjects, string filePath)
    {
        Stack = new Stack<Node>();
        Root = new RootNode();
        Stack.Push(Root);

        ConvertConfToVariables(Root, conf, lineObjects, filePath);

        for (int i = 0; i < lineObjects.Count; i++)
        {
            var lineObject = lineObjects[i];

            if (ParseHeading(lineObject) != null)
            {
                continue;
            }

            if (ParseUnorderedList(lineObjects, ref i) != null)
            {
                continue;
            }

            if (ParseHeaderList(lineObject) != null)
            {
                continue;
            }

            if (ParseYamlSection(lineObjects, ref i) != null)
            {
                continue;
            }

            if (ParseVariable(lineObject) != null)
            {
                continue;
            }

        }

        ConvertImageFilePathsToRelative(Root);
        ProcessInitialValues(Root);

        return Root;
    }

    // conf から機能を持った変数を移行
    private void ConvertConfToVariables(Node node, IDictionary<string, object> conf, List<LineObject> lineObjects, string filePath)
    {
        if (conf.ContainsKey("$templateValues") && conf["$templateValues"] is Dictionary<string, object> templateValues)
        {
            foreach (var kvp in templateValues)
            {
                Root.Variables[kvp.Key] = kvp.Value;
            }
        }

        var variableList = new List<string>
        {
            "templateFilename",
            "ignoreColumnId",
            "outputFilename",
            "projectId",
            "indexSheetname",
            "rootDirectory",
        };

        foreach (var key in variableList)
        {
            var confKey = "$" + key;
            if (conf.ContainsKey(confKey) && conf[confKey] != null)
            {
                var value = conf[confKey];
                var targetKey = key;

                if (key == "projectId")
                {
                    targetKey = "project";
                }
                node.Variables[targetKey] = value;
            }
        }

        if (node.Variables.ContainsKey("rootDirectory") && node.Variables["rootDirectory"] is string rootDirectory)
        {
            var basePath = Path.GetDirectoryName(filePath);
            var absolutePath = rootDirectory;
            var relativePath = PathUtils.GetRelativePath(basePath, absolutePath);

            node.Variables["rootDirectory"] = relativePath;
        }

        if (conf.ContainsKey("$input") && conf["$input"] is Dictionary<string, object> input)
        {
            if (input.ContainsKey("order") && input["order"] is List<object> order)
            {
                node.TemporaryVariables["columnNames"] = string.Join(",", order);
            }
            if (input.ContainsKey("defaultValues") && input["defaultValues"] is Dictionary<string, object> defaultValues)
            {
                node.TemporaryVariables["defaultColumnValues"] = JsonSerializer.Serialize(defaultValues);
            }

            if (input.ContainsKey("rules") && input["rules"] is List<object> rules)
            {
                var tempConditionalColumnValues = node.TemporaryVariables.TryGetValue("conditionalColumnValues", out object value)
                    ? value
                    : (node.TemporaryVariables["conditionalColumnValues"] = new List<Dictionary<string, object>>());
                var conditionalColumnValues = tempConditionalColumnValues as List<Dictionary<string, object>>;

                foreach (var ruleObj in rules)
                {
                    if (ruleObj is Dictionary<object, object> rule)
                    {
                        var condition = new Regex(rule["condition"].ToString());
                        var values = rule["values"] as Dictionary<object, object>;

                        conditionalColumnValues.Add(new Dictionary<string, object>
                        {
                            { "re", condition },
                            { "columnValues", values }
                        });
                    }
                }
            }
        }
    }

    public HeaderNode ParseHeading(LineObject lineObj)
    {
        string line = lineObj.Line;
        Match h = Regex.Match(line, @"^(#+)\s+(.*)$");

        if (!h.Success)
        {
            return null;
        }

        int level = h.Groups[1].Value.Length;
        string text = h.Groups[2].Value;

        while (Stack.Peek().Kind != NodeKind.H || Stack.Peek().Level >= level)
        {
            Stack.Pop();
        }

        string uid = null;
        //List<TableHeader> tableHeaders = null;
        List<TableHeaderNonInputArea> tableHeadersNonInputArea = null;
        string url = null;

        if (level == 1)
        {
            Match uidMatch = Regex.Match(text, @"^\[#([\w-]+)\]\s*(.+)$");

            if (uidMatch.Success)
            {
                uid = uidMatch.Groups[1].Value;
                text = uidMatch.Groups[2].Value;

                Dictionary<string, LineObject> uidListH1 = FindUidList(Stack.Peek());
                if (uidListH1.ContainsKey(uid))
                {
                    LineObject uidInfo0 = uidListH1[uid];
                    string errorMessage = "ID '#" + uid + "' が重複しています";
                    errorMessage += MakeLineinfoString(uidInfo0.FilePath, uidInfo0.LineNumber);
                    errorMessage += MakeLineinfoString(lineObj.FilePath, lineObj.LineNumber);

                    throw new Exception(errorMessage);
                }
                else
                {
                    uidListH1[uid] = lineObj;
                }
            }

            tableHeadersNonInputArea = new List<TableHeaderNonInputArea>();
        }
        else
        {
            while (Regex.IsMatch(text, @".*\s\+\s*$"))
            {
                // 次の行の取得処理が不明
            }

            Match link = Regex.Match(text.Trim(), @"^\[(.+)\]\((.+)\)$");
            if (link.Success)
            {
                text = link.Groups[1].Value.Trim();
                url = link.Groups[2].Value.Trim();
            }
        }

        text = text.Trim();

        if (text.Length > 31)
        {
            string errorMessage = "シート名が31文字を超えています";
            throw new Exception(errorMessage);
        }
        if (Regex.IsMatch(text, @"[:\\?\[\]/*：￥？［］／＊]"))
        {
            string errorMessage = "シート名に使用できない文字が含まれています"
                + "\n\nシート名には全角、半角ともに次の文字は使えません"
                + "\n1 ）コロン　　　　："
                + "\n2 ）円記号　　　　￥"
                + "\n3 ）疑問符　　　　？"
                + "\n4 ）角カッコ　　　[ ]"
                + "\n5 ）スラッシュ　　 /"
                + "\n6 ）アスタリスク　＊";
            throw new Exception(errorMessage);
        }
        if (Root.Children.FirstOrDefault(n => n.Text == text) != null)
        {
            string errorMessage = "シート名「" + text + "」はすでに使われています";
            throw new Exception(errorMessage);
        }

        HeaderNode item = new HeaderNode
        {
            Level = level,
            Id = uid,
            Text = text,
            Variables = new Dictionary<string, object>(), // variables の使用方法が不明
            Children = new List<Node>(),
            TableHeadersNonInputArea = tableHeadersNonInputArea,
            Url = url,
            SrcHash = null,
        };

        item.TemporaryVariables["uidList"] = new Dictionary<string, LineObject>();
        item.TemporaryVariables["lineObj"] = lineObj;

        AddChildNode(Stack.Peek(), item);
        Stack.Push(item);

        if (level == 1 && uid == null)
        {
            string newSrcText = lineObj.Line;
            Match match = Regex.Match(newSrcText, @"^(#+)(?: \[#[\w-]+\])?(.*)$");

            newSrcText = match.Groups[1].Value + " [#{uid}]" + match.Groups[2].Value;

//            if (lineObj.Comment != null)
//            {
//                newSrcText += lineObj.Comment;
//            }

            AddNoIdNode(item, lineObj, lineObj.LineNumber, newSrcText);
        }

        return item;
    }

    public void AddChildNode(Node parent, Node child)
    {
        parent.Children.Add(child);
        child.Parent = parent;
    }

    public void AddNoIdNode(Node node, LineObject lineObj, int lineNum, string newSrcText)
    {
        string filePath = lineObj.FilePath;
        string projectDirectory = lineObj.ProjectDirectory;
        string key = projectDirectory + ":" + filePath;

        if (!NoIdNodes.ContainsKey(key))
        {
            NoIdNodes[key] = new List<NoIdNodeData>();
        }

        NoIdNodes[key].Add(new NoIdNodeData
        {
            Node = node,
            LineObj = lineObj,
            LineNum = lineNum,
            NewSrcText = newSrcText
        });
    }

    public string MakeLineinfoString(string filePath, int lineNum)
    {
        string s = "";

        if (filePath == null)
        {
            return s;
        }

        s += "\nファイル:\t" + filePath;

        if (lineNum == 0)
        {
            return s;
        }

        s += "\n行:\t" + lineNum;

        return s;
    }

    public Node FindParentNode(Node parent, Func<Node, bool> func)
    {
        for (; parent != null; parent = parent.Parent)
        {
            if (func(parent))
            {
                return parent;
            }
        }
        return null;
    }

    public Dictionary<string, LineObject> FindUidList(Node parent)
    {
        Node node = FindParentNode(parent, n => n.TemporaryVariables.ContainsKey("uidList"));
        return node?.TemporaryVariables["uidList"] as Dictionary<string, LineObject>;
    }

    public HeaderNode ParseYamlSection(List<LineObject> lineObjects, ref int index)
    {
        if (!Regex.IsMatch(lineObjects[index].Line, @"^\s*```yaml\s*$"))
        {
            return null;
        }

        Node parent = Stack.Peek();
        string s = "";

        index++;
        for (; index < lineObjects.Count; index++)
        {
            var lineObject = lineObjects[index];
            var line = lineObject.Line;

            if (Regex.IsMatch(line, @"^\s*```\s*$"))
            {
                break;
            }
            s += line + "\n";
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        Dictionary<string, object> o;
        try
        {
            o = deserializer.Deserialize<Dictionary<string, object>>(s);
        }
        catch (Exception e)
        {
            throw new Exception("YAML の parse に失敗しました。", e);
        }

        var jsObject = ConvertToJsObject(o, _engine);
        _engine.Script.myObject = jsObject;
        //ConvertFunctions(o);

        // FIXME: これも JS 内でやるべきこと
        Node paramNode = parent;
        while (paramNode.Level != 1)
        {
            paramNode = paramNode.Parent;
        }

        if (!paramNode.TemporaryVariables.ContainsKey("params"))
        {
            paramNode.TemporaryVariables["params"] = new Dictionary<string, object>();
        }

        paramNode.TemporaryVariables["params"] = DeepMerge(
            (Dictionary<string, object>)paramNode.TemporaryVariables["params"], o);

        return paramNode as HeaderNode;
    }

    private object ConvertToJsObject(object data, V8ScriptEngine engine)
    {
        if (data is Dictionary<string, object> dict)
        {
            var jsDict = new ExpandoObject();
            var jsDictDict = (IDictionary<string, object>)jsDict;
            foreach (var kvp in dict)
            {
                jsDictDict[kvp.Key] = ConvertToJsObject(kvp.Value, engine);
                if (kvp.Value is string str && str.TrimStart().StartsWith("function"))
                {
                    engine.Execute($"var {kvp.Key} = {str};");
                    jsDictDict[kvp.Key] = engine.Script[kvp.Key];
                }
            }
            return jsDict;
        }
        else if (data is List<object> list)
        {
            return list.Select(item => ConvertToJsObject(item, engine)).ToList();
        }
        else
        {
            return data; // 基本データ型やその他はそのまま返す
        }
    }

    private void ConvertFunctions(Dictionary<string, object> o)
    {
        foreach (var key in o.Keys)
        {
            if (o[key] is string str && str.StartsWith("function"))
            {
                o[key] = new Func<object>(() => str);
            }
            else if (o[key] is Dictionary<string, object> dict)
            {
                ConvertFunctions(dict);
            }
        }
    }

    private Dictionary<string, object> DeepMerge(Dictionary<string, object> a, Dictionary<string, object> b)
    {
        foreach (var key in b.Keys)
        {
            if (a.ContainsKey(key) && a[key] is Dictionary<string, object> aDict && b[key] is Dictionary<string, object> bDict)
            {
                a[key] = DeepMerge(aDict, bDict);
            }
            else
            {
                a[key] = b[key];
            }
        }
        return a;
    }


    public ItemNode ParseUnorderedList(List<LineObject> lineObjects, ref int index)
    {
        LineObject lineObj = lineObjects[index];
        string line = lineObj.Line;

        // 行頭に全角スペース、タブがないかのチェック
        {
            var fullwidthSpaceMatch = Regex.Match(line, @"^([\s　]+).*");
            if (fullwidthSpaceMatch.Success)
            {
                var regex = new Regex("[　\t]");
                if (regex.IsMatch(fullwidthSpaceMatch.Groups[1].Value))
                {
                    var errorMessage = "行頭に全角スペースもしくはタブ文字が含まれています";
                    throw new ParseError(errorMessage, lineObj);
                }
            }
        }

        // # とか - とか 1. の後ろにスペースがないかのチェック
        void CheckSpaceAfterMark(Regex re)
        {
            var spaceMatch = re.Match(line);
            if (spaceMatch.Success)
            {
                var regex = new Regex(@"^\s+");
                if (!regex.IsMatch(spaceMatch.Groups[1].Value))
                {
                    var errorMessage = "行頭の記号の後ろに半角スペースが必要です";
                    throw new ParseError(errorMessage, lineObj);
                }
            }
        }

        CheckSpaceAfterMark(new Regex(@"^#+(.+)$"));
        CheckSpaceAfterMark(new Regex(@"^\s*[\*\+\-]\.?(.+)$"));
        CheckSpaceAfterMark(new Regex(@"^\s*\d+\.(.+)$"));

        var ul = Regex.Match(line, @"^(\s*)([\*\+\-])\s+(.*)$");

        if (!ul.Success)
        {
            return null;
        }

        var indent = ul.Groups[1].Length;
        var text = ul.Groups[3].Value;
        var marker = ul.Groups[2].Value;

        while (Stack.Peek().Kind == NodeKind.UL && ((ItemNode)Stack.Peek()).Indent >= indent)
        {
            Stack.Pop();
        }
        if (Stack.Peek().Kind != NodeKind.UL && indent > 0)
        {
            var errorMessage = "一番上の階層のノードがインデントされています";
            throw new ParseError(errorMessage, lineObj);
        }
        if (Stack.Peek().Kind == NodeKind.UL && (indent - ((ItemNode)Stack.Peek()).Indent < 2))
        {
            var errorMessage = "インデントはスペース 2 個以上必要です";
            throw new ParseError(errorMessage, lineObj);
        }

        var uidMatch = Regex.Match(text, @"^\[#([\w\-]+)\]\s+(.+)$");
        string uid = null;
        if (uidMatch.Success)
        {
            uid = uidMatch.Groups[1].Value;
            text = uidMatch.Groups[2].Value;
            var uidList = FindUidList(Stack.Peek());
            if (uidList.ContainsKey(uid))
            {
                var uidInfo0 = uidList[uid];
                var errorMessage = $"ID '#{uid}' が重複しています";
                errorMessage += MakeLineinfoString(uidInfo0.FilePath, uidInfo0.LineNumber);
                errorMessage += MakeLineinfoString(lineObj.FilePath, lineObj.LineNumber);
                throw new ParseError(errorMessage, lineObj);
            }
            else
            {
                uidList[uid] = lineObj;
            }
        }

        Dictionary<string, string> attributes = null;
        while (true)
        {
            var attributeMatch = Regex.Match(text, @"^\s*\<([A-Za-z_]\w*)\>\(([^\)]+)\)\s*(.+)$");
            if (!attributeMatch.Success)
            {
                break;
            }
            var name = attributeMatch.Groups[1].Value;
            var value = attributeMatch.Groups[2].Value;
            text = attributeMatch.Groups[3].Value;

            if (attributes == null)
            {
                attributes = new Dictionary<string, string>();
            }
            attributes[name] = value;
        }

        Dictionary<string, object> initialValues = null;

        Dictionary<string, object> GetLowestColumnNames()
        {
            foreach (var elem in Stack)
            {
                if (elem.Variables.ContainsKey("columnNames"))
                {
                    return new Dictionary<string, object>
                    {
                        { "columnNames", elem.Variables["columnNames"] },
                        { "defaultColumnValues", elem.Variables["defaultColumnValues"] }
                    };
                }
            }
            return null;
        }

#if false
        {
            var parse = ParseColumnValues(text, true);
            if (parse != null)
            {
                text = parse.Remain;

                if (initialValues == null)
                {
                    initialValues = new Dictionary<string, object>();
                }

                var columnNames = GetLowestColumnNames()?["columnNames"] as List<string>;

                foreach (var param in parse.ColumnValues)
                {
                    var value = param.Value ?? null;
                    if (param.Key != null)
                    {
                        initialValues[param.Key] = value;
                        continue;
                    }
                    if (columnNames == null)
                    {
                        var errorMessage = "列名リストが宣言されていません。";
                        throw new ParseError(errorMessage, lineObj);
                    }
                    if (parse.ColumnValues.IndexOf(param) >= columnNames.Count)
                    {
                        var errorMessage = "列の初期値が列名リストの範囲外に指定されています。";
                        throw new ParseError(errorMessage, lineObj);
                    }
                    var key = columnNames[parse.ColumnValues.IndexOf(param)];
                    initialValues[key] = value;
                }
            }
        }
#endif

        while (Regex.IsMatch(text, @".*\s\+\s*$"))
        {
            index++;
            lineObj = lineObjects[index];
            line = lineObj.Line;

            line = line.TrimStart();
            text = text.TrimEnd().TrimEnd('+') + "\n" + line;
        }

        var commentResult = ParseComment(text, lineObj);
        string comment = null;
        string imageFilePath = null;
        if (commentResult != null)
        {
            text = commentResult.Text;
            comment = commentResult.Comment;
            imageFilePath = commentResult.ImageFilePath;
        }

#if false
        var td = Regex.Match(text, @"^([^\|]+)\|(.*)\|$");
        List<string> data = null;
        if (td.Success)
        {
            text = td.Groups[1].Value.Trim();
            data = new List<string>(td.Groups[2].Value.Split('|'));
            for (int i = 0; i < data.Count; i++)
            {
                data[i] = data[i].Trim();
            }

            data = GetDataFromTableRow(data, Stack.Peek());

            if (data == null)
            {
                var errorMessage = "シートに該当IDの確認欄がありません";
                throw new ParseError(errorMessage, lineObj);
            }
        }
#endif

        var link = Regex.Match(text.Trim(), @"^\[(.+)\]\((.+)\)$");
        string url = null;
        if (link.Success)
        {
            text = link.Groups[1].Value.Trim();
            url = link.Groups[2].Value.Trim();
        }

        text = text.Trim();

        if (text.Contains("\t"))
        {
            var errorMessage = "テキストにタブ文字が含まれています";
            throw new ParseError(errorMessage, lineObj);
        }
        if (comment != null && comment.Contains("\t"))
        {
            var errorMessage = "コメントにタブ文字が含まれています";
            throw new ParseError(errorMessage, lineObj);
        }

        var item = new ItemNode
        {
            Indent = indent,
            Marker = marker,
            Group = -1,
            DepthInGroup = -1,
            Id = uid,
            Text = text,
            Comment = comment,
            ImageFilePath = imageFilePath,
            InitialValues = initialValues,
            Attributes = attributes,
            Url = url
        };

        item.TemporaryVariables["lineObj"] = lineObj;

        AddChildNode(Stack.Peek(), item);
        Stack.Push(item);

        if (!uidMatch.Success)
        {
            var newSrcText = lineObj.Line;
            var match = Regex.Match(newSrcText, @"^(\s*[\*\+\-])(?: \[#[\w\-]+\]\s+)?(.*)$");

            newSrcText = $"{match.Groups[1].Value} [#{uid}]{match.Groups[2].Value}";

            //if (!string.IsNullOrEmpty(lineObj.Comment))
            //{
            //    newSrcText += lineObj.Comment;
            //}

            AddNoIdNode(item, lineObj, lineObj.LineNumber, newSrcText);
        }

        return item;
    }

#if false
    public List<string> GetDataFromTableRow(List<string> srcData, Node parentNode, List<int> tableHeaderIds = null)
    {
        var data = new List<string>();

        // H1は確実に見つかるものとしてOK
        var h1Node = FindParentNode(parentNode, node => node.Kind == NodeKind.H && node.Level == 1) as HeaderNode;

        if (h1Node == null)
        {
            throw new Exception("H1 node not found.");
        }

        if (tableHeaderIds == null)
        {
            // 「必ず 1 から始まる連番」の方が仕様として素直ですっきりしているか
            var minNumber = 1;

            tableHeaderIds = new List<int>();
            for (var i = 0; i < srcData.Count; i++)
            {
                tableHeaderIds.Add(minNumber + i);
            }
        }

        for (var i = 0; i < srcData.Count; i++)
        {
            if (string.IsNullOrEmpty(srcData[i]))
            {
                continue;
            }

            var number = tableHeaderIds[i];
            if (number == 0)
            {
                return null;
            }

            var headerIndex = h1Node.TableHeaders.FindIndex(element => element.Id == number);

            if (headerIndex == -1)
            {
                return null;
            }

            data[headerIndex] = srcData[i];
        }

        return data;
    }
#endif

    private dynamic ParseComment(string text, LineObject lineObj)
    {
        var projectDirectoryFromRoot = lineObj.ProjectDirectory;
        var fileParentFolderAbs = pathHelper.SourceLocalPathToAbsolutePath(Path.GetDirectoryName(lineObj.FilePath), projectDirectoryFromRoot);

        // 複数行テキストに対応するために .+ じゃなくて [\s\S]+
        var re = new Regex(@"^([\s\S]+)\s+\[\^(.+)\]$");
        var commentMatch = re.Match(text.Trim());

        if (!commentMatch.Success)
        {
            return null;
        }

        text = commentMatch.Groups[1].Value.Trim();
        var comment = commentMatch.Groups[2].Value.Trim();

        comment = comment.Replace("<br>", "\n").Replace("\\n", "\n");

        var imageMatch = Regex.Match(comment, @"^\!(.+)\!$");

        if (!imageMatch.Success)
        {
            return new
            {
                Text = text,
                Comment = comment,
                ImageFilePath = (string)null,
            };
        }

        var imageFilePath = imageMatch.Groups[1].Value;

        return new
        {
            Text = text,
            Comment = (string)null,
            ImageFilePath = imageFilePath,
        };
    }

    public List<TableHeaderNonInputArea> ParseHeaderList(LineObject lineObject)
    {
        // "*.", "-.", "+." はチェック項目列の見出しとする
        string line = lineObject.Line;
        var headerList = Regex.Match(line, @"^(?:\s*)([\*\+\-])\.\s+(.*)\s*$");
        if (!headerList.Success)
        {
            return null;
        }

        var parent = Stack.Peek();

        // 現状は H1 の直下専用
        if (parent.Kind != NodeKind.H || parent.Level != 1)
        {
            throw new InvalidOperationException("Parent node is not a HeaderNode or is not at level 1.");
        }

        var headerParent = parent as HeaderNode;
        if (headerParent == null)
        {
            throw new InvalidOperationException("Parent node is not a HeaderNode.");
        }

        var text = headerList.Groups[2].Value;

        string comment = null;
        var commentMatch = Regex.Match(text, @"^(.+)\s*\[\^(.+)\]$");
        if (commentMatch.Success)
        {
            text = commentMatch.Groups[1].Value.Trim();
            comment = commentMatch.Groups[2].Value.Trim();
            comment = comment.Replace("<br>", "\n");
        }

        var headers = headerParent.TableHeadersNonInputArea;
        var prevName = (headers.Count >= 1) ? headers[headers.Count - 1].Name : null;

        if (string.IsNullOrEmpty(text) || (prevName != null && text == prevName))
        {
            headers[headers.Count - 1].Size++;
        }
        else
        {
            var marker = headerList.Groups[1].Value[0];  // char として取得

            var group = 0;
            if (headers.Count >= 1)
            {
                group = headers[headers.Count - 1].Group;
                if (headers[headers.Count - 1].Marker != marker)
                {
                    group++;
                }
            }

            var item = new TableHeaderNonInputArea
            {
                Marker = marker,
                Group = group,
                Name = text,
                Comment = comment,
                Size = 1
            };

            headers.Add(item);
        }

        return headers;
    }

    string ParseVariable(LineObject lineObject)
    {
        // 正規表現で [key]: value の形式を解析
        string line = lineObject.Line;
        var regex = new Regex(@"^\s*\[(.+)\]:\s+(.+)$");
        var match = regex.Match(line);

        if (!match.Success)
        {
            return null;
        }
        string key = match.Groups[1].Value.Trim();
        string value = match.Groups[2].Value.Trim();
        Stack.Peek().Variables[key] = value;

        return value;
    }

    void ConvertImageFilePathsToRelative(Node root)
    {
        ForAllNodesRecurse(root, null, -1, (node, parent, index) =>
        {
            if (!(node is ItemNode itemNode))
            {
                return false;
            }

            if (string.IsNullOrEmpty(itemNode.ImageFilePath))
            {
                return false;
            }

            var lineObj = itemNode.TemporaryVariables["lineObj"] as LineObject;
            var projectDirectoryFromRoot = lineObj.ProjectDirectory;
            var fileParentFolderAbs = pathHelper.SourceLocalPathToAbsolutePath(Path.GetDirectoryName(lineObj.FilePath), projectDirectoryFromRoot);

            string GetImageFilePathFromEntryProject(string imageFilePath)
            {
                if (imageFilePath[0] != '/')
                {
                    imageFilePath = Path.Combine(fileParentFolderAbs, imageFilePath);
                }
                else
                {
                    imageFilePath = pathHelper.GetAbsoluteProjectPath(imageFilePath.Substring(1));
                }

                return pathHelper.AbsolutePathToSourceLocalPath(imageFilePath, projectDirectoryFromRoot);
            }

            itemNode.ImageFilePath = GetImageFilePathFromEntryProject(itemNode.ImageFilePath);

            return false;
        }, null);
    }

    static void ProcessInitialValues(Node root)
    {
        var columnNamesStack = new Stack<Dictionary<string, object>>();
        var defaultValuesStack = new Stack<Dictionary<string, object>>();
        var conditionalColumnValuesStack = new Stack<List<Dictionary<string, object>>>();

        columnNamesStack.Push(new Dictionary<string, object>());
        defaultValuesStack.Push(new Dictionary<string, object>());
        conditionalColumnValuesStack.Push(new List<Dictionary<string, object>>());

        ForAllNodesRecurse(root, null, -1,
            (node, parent, index) => // preChildren
            {
                if (node.Children.Count != 0)
                {
                    if (node.TemporaryVariables.ContainsKey("columnNames") && node.TemporaryVariables["columnNames"] is Dictionary<string, object> columnNames)
                    {
                        columnNamesStack.Push(columnNames);
                    }

                    if (node.TemporaryVariables.ContainsKey("defaultColumnValues") && node.TemporaryVariables["defaultColumnValues"] is Dictionary<string, object> defaultColumnValues)
                    {
                        var value = new Dictionary<string, object>(defaultValuesStack.Peek());
                        foreach (var kvp in defaultColumnValues)
                        {
                            value[kvp.Key] = kvp.Value;
                        }
                        defaultValuesStack.Push(value);
                    }

                    if (node.TemporaryVariables.ContainsKey("conditionalColumnValues") && node.TemporaryVariables["conditionalColumnValues"] is List<Dictionary<string, object>> conditionalColumnValues)
                    {
                        var value = conditionalColumnValues.Concat(conditionalColumnValuesStack.Peek()).ToList();
                        conditionalColumnValuesStack.Push(KeyValuePairToObject(value, columnNamesStack.Peek()));
                    }

#if false
                    // XXX: 本来はエラーとすべきだけど、一旦削除するようにしておく
                    // XXX: 仕様変更するかも
                    if (node.TemporaryVariables.ContainsKey("initialValues"))
                    {
                        node.TemporaryVariables.Remove("initialValues");
                    }
#endif
                }
                else
                {
                    var leafNode = node as ItemNode;

                    if (leafNode.InitialValues == null)
                    {
                        leafNode.InitialValues = new Dictionary<string, object>();
                    }

                    foreach (var elem in conditionalColumnValuesStack.Peek())
                    {
                        var columnValues = new Dictionary<string, object>();
                        foreach (var kvp in elem["columnValues"] as Dictionary<object, object>)
                        {
                            var key = kvp.Key.ToString();
                            if (!leafNode.InitialValues.ContainsKey(key))
                            {
                                columnValues[key] = kvp.Value;
                            }
                        }

                        if (columnValues.Count != 0 && (elem["re"] as Regex).IsMatch(node.Text))
                        {
                            foreach (var kvp in columnValues)
                            {
                                leafNode.InitialValues[kvp.Key] = kvp.Value;
                            }
                        }
                    }

                    foreach (var kvp in defaultValuesStack.Peek())
                    {
                        if (!leafNode.InitialValues.ContainsKey(kvp.Key))
                        {
                            leafNode.InitialValues[kvp.Key] = kvp.Value;
                        }
                    }

                    var initialValues = leafNode.InitialValues;
                    foreach (var key in initialValues.Keys.ToList())
                    {
                        if (initialValues[key] is string strValue && string.IsNullOrEmpty(strValue))
                        {
                            initialValues.Remove(key);
                        }
                    }

                    if (initialValues.Count == 0)
                    {
                        leafNode.InitialValues = null;
                    }
                }
                return false;
            },
            (node, parent, index) => // postChildren
            {
                if (node.Children.Count != 0)
                {
                    if (node.TemporaryVariables.ContainsKey("columnNames"))
                    {
                        columnNamesStack.Pop();
                    }

                    if (node.TemporaryVariables.ContainsKey("defaultColumnValues"))
                    {
                        defaultValuesStack.Pop();
                    }

                    if (node.TemporaryVariables.ContainsKey("conditionalColumnValues"))
                    {
                        conditionalColumnValuesStack.Pop();
                    }
                }
            }
        );
    }

    private static bool ForAllNodesRecurse(Node node, Node parent, int index, Func<Node, Node, int, bool> preChildren, Action<Node, Node, int> postChildren)
    {
        if (preChildren(node, parent, index))
        {
            return true;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            if (node.Children[i] == null)
            {
                continue;
            }
            ForAllNodesRecurse(node.Children[i], node, i, preChildren, postChildren);
        }

        postChildren?.Invoke(node, parent, index);

        return false;
    }

    private static List<Dictionary<string, object>> KeyValuePairToObject(List<Dictionary<string, object>> conditionalColumnValues, Dictionary<string, object> currentColumnNames)
    {
        var result = new List<Dictionary<string, object>>();

        foreach (var conditionalColumnValue in conditionalColumnValues)
        {
            var re = conditionalColumnValue["re"] as Regex;
            if (!(conditionalColumnValue["columnValues"] is List<object> columnValuesData))
            {
                result.Add(new Dictionary<string, object>
                {
                    { "re", re },
                    { "columnValues", conditionalColumnValue["columnValues"] }
                });
                continue;
            }

            var columnValues = new Dictionary<string, object>();
            for (int i = 0; i < columnValuesData.Count; i++)
            {
                if (columnValuesData[i] is Dictionary<string, object> element)
                {
                    if (!element.ContainsKey("key"))
                    {
                        if (i >= currentColumnNames.Count)
                        {
                            // エラー処理（例: 例外をスローする）
                            throw new Exception("初期値が列名リストの範囲外に設定されています。");
                        }
                        var key = currentColumnNames.Keys.ToList()[i];
                        columnValues[key] = element["value"];
                    }
                    else
                    {
                        columnValues[element["key"].ToString()] = element["value"];
                    }
                }
            }

            result.Add(new Dictionary<string, object>
            {
                { "re", re },
                { "columnValues", columnValues }
            });
        }

        return result;
    }

}
