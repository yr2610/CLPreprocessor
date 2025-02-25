using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

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
    public NodeKind Kind { get; set; }
    public int Level { get; set; } = 0;
    public string Id { get; set; }   // sourceFiles の last modified date を基に生成した id を埋め込む
    public string Text { get; set; } = "";
    public Dictionary<string, object> Variables { get; set; } = new Dictionary<string, object>();
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
        // UID重複チェック用
        // 「複数人で１つのファイルを作成（ID自動生成）してマージしたら衝突」は考慮しなくて良いぐらいの確率だけど、「IDごとコピペして重複」が高頻度で発生する恐れがあるので
        TemporaryVariables["uidList"] = new Dictionary<string, LineObject>();
    }
}

public class TableHeader
{
    public string Name { get; set; }
    public int Id { get; set; }
}

public class TableHeaderNonInputArea
{
    public int Group { get; set; }
    public string Name { get; set; }
    public int Size { get; set; }
}

public class HeaderNode : Node
{
    public HeaderNode() : base(NodeKind.H)
    {
    }

    public List<TableHeader> TableHeaders { get; set; } = new List<TableHeader>();
    public List<TableHeaderNonInputArea> TableHeaderNonInputArea { get; set; } = new List<TableHeaderNonInputArea>();

    public string Url { get; set; }
    public string SrcHash { get; set; }
}

public class ItemNode : Node
{
    public ItemNode() : base(NodeKind.UL)
    {
    }

    public int Indent { get; set; }
    public string Marker { get; set; }
    public int Group { get; set; }
    public int DepthInGroup { get; set; }
    public List<string> TableData { get; set; }
    public string Comment { get; set; }
    public string ImageFilePath { get; set; }
    public Dictionary<string, object> InitialValues { get; set; }
    public Dictionary<string, string> Attributes { get; set; }
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
    public Stack<Node> Stack { get; set; }
    public Node Root { get; set; }
    public Dictionary<string, List<NoIdNodeData>> NoIdNodes { get; set; } = new Dictionary<string, List<NoIdNodeData>>();

    PathHelper pathHelper;

    public Parser(string rootDirectory)
    {
        pathHelper = new PathHelper(rootDirectory);
    }

    public void ParseLines(IDictionary<string, object> conf, List<LineObject> lineObjects, string filePath)
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

            if (ParseUnorderedList(lineObject) != null)
            {
                continue;
            }

            if (Regex.IsMatch(lineObject.Line, @"^\s*```yaml\s*$"))
            {
                ParseYamlSection(lineObjects, ref i);
            }
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        string jsonString = JsonSerializer.Serialize(Root, options);

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
                node.TemporaryVariables[targetKey] = value;
            }
        }

        if (node.TemporaryVariables.ContainsKey("rootDirectory") && node.TemporaryVariables["rootDirectory"] is string rootDirectory)
        {
            var basePath = Path.GetDirectoryName(filePath);
            var absolutePath = rootDirectory;
            var relativePath = PathUtils.GetRelativePath(basePath, absolutePath);

            node.TemporaryVariables["rootDirectory"] = relativePath;
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
                var conditionalColumnValues = node.TemporaryVariables.ContainsKey("conditionalColumnValues") && node.TemporaryVariables["conditionalColumnValues"] is string conditionalColumnValuesJson ?
                    JsonSerializer.Deserialize<List<Dictionary<string, object>>>(conditionalColumnValuesJson) :
                    new List<Dictionary<string, object>>();

                foreach (var ruleObj in rules)
                {
                    if (ruleObj is Dictionary<string, object> rule)
                    {
                        var condition = rule["condition"].ToString();
                        var values = rule["values"] as Dictionary<string, object>;

                        conditionalColumnValues.Add(new Dictionary<string, object>
                    {
                        { "re", condition },
                        { "columnValues", values }
                    });
                    }
                }
                node.TemporaryVariables["conditionalColumnValues"] = JsonSerializer.Serialize(conditionalColumnValues);
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
        List<TableHeader> tableHeaders = null;
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

            tableHeaders = new List<TableHeader>();
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
            TableHeaders = tableHeaders,
            TableHeaderNonInputArea = tableHeadersNonInputArea,
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

    public void ParseYamlSection(List<LineObject> lineObjects, ref int index)
    {
        Node parent = Stack.Peek();
        string s = "";
        bool isYamlSection = false;

        for (; index < lineObjects.Count; index++)
        {
            var lineObject = lineObjects[index];
            var line = lineObject.Line;

            if (Regex.IsMatch(line, @"^\s*```yaml\s*$"))
            {
                isYamlSection = true;
                continue;
            }

            if (isYamlSection)
            {
                if (Regex.IsMatch(line, @"^\s*```\s*$"))
                {
                    isYamlSection = false;
                    break;
                }
                s += line + "\n";
            }
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

        ConvertFunctions(o);

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


    public ItemNode ParseUnorderedList(LineObject lineObj)
    {
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
            TableData = data,
            Comment = comment,
            ImageFilePath = imageFilePath,
            InitialValues = initialValues,
            Attributes = attributes,
            Url = url
        };

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

}
