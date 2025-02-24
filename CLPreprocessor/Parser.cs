using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

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

    [JsonIgnore] // JSON シリアライズ時に無視
    public Node Parent { get; set; }
    public Dictionary<string, object> TemporaryVariables { get; set; } = new Dictionary<string, object>();

}

public class RootNode : Node
{
    public RootNode() : base(NodeKind.H)
    {
    }

    // 以下はJSON出力前に削除する
    // UID重複チェック用

    // 「複数人で１つのファイルを作成（ID自動生成）してマージしたら衝突」は考慮しなくて良いぐらいの確率だけど、「IDごとコピペして重複」が高頻度で発生する恐れがあるので
    [JsonIgnore]
    List<UidInfo> UidList { get; set; }
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

public class NoIdNodeData
{
    public Node Node { get; set; }
    public LineObject LineObj { get; set; }
    public int LineNum { get; set; }
    public string NewSrcText { get; set; }
}

public class Parser
{
    public Stack<Node> Stack { get; set; }
    public Node Root { get; set; }
    public Dictionary<string, List<NoIdNodeData>> NoIdNodes { get; set; } = new Dictionary<string, List<NoIdNodeData>>();

    public void ParseLines(IDictionary<string, object> conf, List<LineObject> lineObjects, string filePath)
    {
        Stack = new Stack<Node>();
        Root = new RootNode();
        Stack.Push(Root);

        ConvertConfToVariables(Root, conf, lineObjects, filePath);
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

    public Node ParseHeading(LineObject lineObj)
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
                    errorMessage += MakeLineinfoString(uidInfo0.FilePath, uidInfo0.StartLineNumber);
                    errorMessage += MakeLineinfoString(lineObj.FilePath, lineObj.StartLineNumber);

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

            AddNoIdNode(item, lineObj, lineObj.StartLineNumber, newSrcText);
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
}
