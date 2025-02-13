using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

public class LineObject
{
    public int StartLineNumber { get; set; }
    public int EndLineNumber { get; set; }
    public List<string> Lines { get; set; }
    public int OriginalStartLineNumber { get; set; }
    public string OriginalFilePath { get; set; }
}

public class FormulaParser
{
    private string formula;
    private HashSet<string> defines;
    private List<string> tokens;
    private int position = 0;

    public FormulaParser(string formula, HashSet<string> defines)
    {
        this.formula = formula.Trim();
        this.defines = defines;
        this.tokens = Tokenize(formula);
    }

    public bool Evaluate()
    {
        position = 0;
        return ParseExpression();
    }

    private List<string> Tokenize(string formula)
    {
        // スペースを除去しつつトークン化
        var tokenRegex = new Regex(@"(?<=\s|^)(true|false|[a-zA-Z_]\w*|\|\||&&|!|\(|\))(?=\s|$)");
        return tokenRegex.Matches(formula).Cast<Match>().Select(m => m.Value).ToList();
    }

    private bool ParseExpression()
    {
        bool result = ParseOr();
        return result;
    }

    private bool ParseOr()
    {
        bool result = ParseAnd();
        while (position < tokens.Count && tokens[position] == "||")
        {
            position++;
            result = result || ParseAnd();
        }
        return result;
    }

    private bool ParseAnd()
    {
        bool result = ParseNot();
        while (position < tokens.Count && tokens[position] == "&&")
        {
            position++;
            result = result && ParseNot();
        }
        return result;
    }

    private bool ParseNot()
    {
        if (position < tokens.Count && tokens[position] == "!")
        {
            position++;
            return !ParseNot();
        }
        return ParsePrimary();
    }

    private bool ParsePrimary()
    {
        if (position < tokens.Count)
        {
            if (tokens[position] == "(")
            {
                position++;
                bool result = ParseExpression();
                if (position >= tokens.Count || tokens[position] != ")")
                    throw new ArgumentException("Unmatched parentheses in formula");
                position++;
                return result;
            }

            if (tokens[position] == "true") { position++; return true; }
            if (tokens[position] == "false") { position++; return false; }

            if (Regex.IsMatch(tokens[position], @"^[a-zA-Z_]\w*$"))
            {
                bool result = defines.Contains(tokens[position]);
                position++;
                return result;
            }

            throw new ArgumentException($"Unexpected token at position {position}: {tokens[position]}");
        }
        throw new ArgumentException("Unexpected end of expression");
    }
}

public class Preprocessor
{
    class State
    {
        public bool Cond { get; set; }
        public bool ElseApplied { get; set; }
        public bool CondDisabled { get; set; }
        public LineObject LineObj { get; set; }
    }

    public class ParseException : Exception
    {
        public LineObject LineObj { get; }

        public ParseException(string message, LineObject lineObj) : base(message)
        {
            LineObj = lineObj;
        }
    }

    private string rootDirectory;
    private HashSet<string> defines = new HashSet<string>();
    private Stack<State> states = new Stack<State>();

    public Preprocessor(string rootDirectory)
    {
        this.rootDirectory = rootDirectory;
    }

#if false
    private class ArrayReader
    {
        private List<LineObject> lines;
        private int index = 0;

        public ArrayReader(List<LineObject> lines)
        {
            this.lines = lines;
        }

        public bool AtEnd => index >= lines.Count;
        public LineObject Read() => AtEnd ? null : lines[index++];
    }
#endif

    private bool CurrentCondition()
    {
        return !states.Any(state => !state.Cond);
    }

    private void ParseCommand(string command, string option, LineObject lineObj)
    {
        switch (command)
        {
            case "define":
                ParseDefine(option, lineObj);
                break;
            case "undef":
                ParseUndef(option, lineObj);
                break;
            //case "set":
            //    ParseSet(option, lineObj);
            //    break;
            case "if":
                ParseIf(option, lineObj);
                break;
            case "elif":
                ParseElif(option, lineObj);
                break;
            case "else":
                ParseElse(lineObj);
                break;
            case "end":
                ParseEnd(lineObj);
                break;
            case "error":
                ParseError(option, lineObj);
                break;
            default:
                {
                    var errorMessage = $"@{command}: 不明のコマンドです。";
                    //var errorMessage = $"Unknown command @{command}";
                    throw new ParseException(errorMessage, lineObj);
                }
        }
    }

    private bool EvaluateFormula(string formula, HashSet<string> defines)
    {
        try
        {
            var parser = new FormulaParser(formula, defines);
            return parser.Evaluate();
        }
        catch (ArgumentException e)
        {
            throw new ParseException($"Error evaluating formula: {e.Message}", null);
        }
    }

    private void ParseDefine(string name, LineObject lineObj)
    {
        if (!CurrentCondition())
        {
            return;
        }

        if (!Regex.IsMatch(name, @"^[a-zA-Z_]\w*$"))
        {
            var errorMessage = "@define コマンドの文法が正しくありません。";
            //var errorMessage = "@define command syntax is incorrect.";
            throw new ParseException(errorMessage, lineObj);
        }

        if (defines.Contains(name))
        {
            var errorMessage = $"'{name}' は定義済みです。";
            throw new ParseException(errorMessage, lineObj);
        }

        defines.Add(name);
    }

    private void ParseUndef(string name, LineObject lineObj)
    {
        if (!CurrentCondition())
        {
            return;
        }

        if (!Regex.IsMatch(name, @"^([a-zA-Z_]\w*)?$"))
        {
            var errorMessage = "@undef コマンドの文法が正しくありません。";
            //var errorMessage = "@undef command syntax is incorrect.";
            throw new ParseException(errorMessage, lineObj);
        }

        if (!defines.Contains(name))
        {
            var errorMessage = $"'{name}' は定義されていません。";
            throw new ParseException(errorMessage, lineObj);
        }

        // XXX: 何で予約語判定してるのか思い出せない
        if (!IsReservedName(name))
        {
            //if (name in objs) {
            defines.Remove(name);
            //}
        }

        // undef の場合は set false 扱い、にしようと思ったけど undef の後で define で redefine 扱いになるので
        // 素直に削除だけにしておく
        //parseSet(name + " = false", lineObj);
    }

#if false
    // TODO: 移植
    private void ParseSet(string option, LineObject lineObj)
    {
        if (!CurrentCondition()) return;
    
        var optionMatch = Regex.Match(option, @"^([a-zA-Z_]\w*)\s*=\s*(.+)?$");
        if (!optionMatch.Success)
            throw new ParseException("@set command syntax is incorrect.", lineObj);
    
        var name = optionMatch.Groups[1].Value;
        var value = optionMatch.Groups[2].Value;
    
        if (IsReservedName(name))
            throw new ParseException("Reserved word used as variable name.", lineObj);
    
        if (defines.Contains(name))
            throw new ParseException($"Variable {name} is already defined.", lineObj);
    
        try
        {
            defines[name] = EvaluateFormula(value, defines);
        }
        catch
        {
            throw new ParseException($"The right-hand side expression '{value}' is invalid.", lineObj);
        }
    }
#endif

    readonly HashSet<string> reserved = new HashSet<string>
    {
        "break",
        "case",
        "catch",
        "continue",
        "debugger",
        "default",
        "delete",
        "do",
        "else",
        "finally",
        "for",
        "function",
        "if",
        "in",
        "instanceof",
        "new",
        "return",
        "switch",
        "this",
        "throw",
        "try",
        "typeof",
        "var",
        "void",
        "while",
        "with",

        "true",
        "false",
        "undefined",
        "null"
    };

    private bool IsReservedName(string name)
    {
        return reserved.Contains(name);
    }

    bool ParseCondition(string conditionFormula, LineObject lineObj)
    {
        try
        {
            return EvaluateFormula(conditionFormula, defines);
        }
        catch (Exception)
        {
            var errorMessage = "条件式が不正です。";
            throw new ParseException(errorMessage, lineObj);
        }
    }

    private void ParseIf(string option, LineObject lineObj)
    {
        var state = new State
        {
            Cond = false, // 今のフラグ
            ElseApplied = false,
            CondDisabled = true,  // これが立ってたらつねに false 扱い

            LineObj = lineObj,
        };
        if (CurrentCondition())
        {
            var cond = ParseCondition(option, lineObj);
            state.Cond = cond;
            state.CondDisabled = cond;
        }

        states.Push(state);
    }

    private void ParseElif(string option, LineObject lineObj)
    {
        // いきなり elif 出現エラー
        if (states.Count == 0)
        {
            var errorMessage = "対応する if がありません。";
            //var errorMessage = "No corresponding @if for @elif.";
            throw new ParseException(errorMessage, lineObj);
        }

        var state = states.Pop();
        // 今の階層ですでに else が処理済みならエラー
        if (state.ElseApplied)
        {
            var errorMessage = "elif が else の後に存在します。";
            //var errorMessage = "@elif appears after @else.";
            throw new ParseException(errorMessage, lineObj);
        }

        if (state.CondDisabled)
        {
            state.Cond = false;
        }
        else
        {
            var cond = ParseCondition(option, lineObj);
            state.Cond = cond;
            state.CondDisabled = cond;
        }

        states.Push(state);
    }

    private void ParseElse(LineObject lineObj)
    {
        // いきなり else 出現エラー
        if (states.Count == 0)
        {
            var errorMessage = "対応する if がありません。";
            //var errorMessage = "No corresponding @if for @else.";
            throw new ParseException(errorMessage, lineObj);
        }

        var state = states.Pop();
        state.Cond = !state.CondDisabled;
        state.ElseApplied = true;

        states.Push(state);
    }

    private void ParseEnd(LineObject lineObj)
    {
        // いきなり end 出現エラー
        if (states.Count == 0)
        {
            var errorMessage = "対応する if がありません。";
            //var errorMessage = "No corresponding @if for @end.";
            throw new ParseException(errorMessage, lineObj);
        }
        states.Pop();
    }

    private void ParseError(string option, LineObject lineObj)
    {
        if (!CurrentCondition())
        {
            return;
        }

        var errorMessage = option.Length > 0 ? $"@error : '{option}'" : "@error が発生しました。";
        //var errorMessage = option.Length > 0 ? $"@error : '{option}'" : "@error occurred.");

        throw new ParseException(errorMessage, lineObj);
    }

    private string ResolveIncludeFilePath(string includePath, string currentProjectDirectoryFromRoot, string currentFilePathAbs)
    {
        string localPath;
        string projectDirectoryFromRoot;

        var includeMatch = Regex.Match(includePath, @"^((\/)?([^:]+):)?(\.\/)?(.+)$");

        if (!includeMatch.Success)
        {
            throw new ArgumentException("Invalid include path.");
        }

        localPath = includeMatch.Groups[5].Value;
        projectDirectoryFromRoot = includeMatch.Groups[3].Value;
        bool relativeFromCurrent = !string.IsNullOrEmpty(includeMatch.Groups[4].Value);

        if (relativeFromCurrent)
        {
            if (!string.IsNullOrEmpty(projectDirectoryFromRoot))
            {
                throw new ArgumentException("Cannot use relative path with external reference.");
            }
            string currentFileDirectoryAbs = Path.GetDirectoryName(currentFilePathAbs);
            return Path.Combine(currentFileDirectoryAbs, localPath);
        }

        if (string.IsNullOrEmpty(projectDirectoryFromRoot))
        {
            projectDirectoryFromRoot = currentProjectDirectoryFromRoot;
        }
        else
        {
            bool fromRoot = !string.IsNullOrEmpty(includeMatch.Groups[2].Value);
            if (!fromRoot)
            {
                // TODO: Implement include path search if not from root
            }
        }

        return Path.Combine(rootDirectory, projectDirectoryFromRoot, "source", localPath);
    }

    private List<LineObject> ParseInclude(string includeFileString, string includeOptionString, string currentProjectDirectoryFromRoot, string filePathAbs, Dictionary<string, string> variables, LineObject lineObj)
    {
        List<LineObject> includeLines = new List<LineObject>();

        // インクルードするファイルのパスを解決
        string includeFilePath = ResolveIncludeFilePath(includeFileString, currentProjectDirectoryFromRoot, filePathAbs);
        if (!File.Exists(includeFilePath))
        {
            throw new ParseException($"Include file not found: {includeFilePath}", lineObj);
        }

        string[] rawLines = File.ReadAllLines(includeFilePath, Encoding.UTF8);
        HashSet<string> localDefines = new HashSet<string>(this.defines); // 現在の定義をコピー

        // オプションの扱い（必要に応じて）
        if (!string.IsNullOrEmpty(includeOptionString))
        {
            // ここでオプションをパースし、必要に応じてアクションを取る
            // 例: オプションが "define NAME" の場合、NAME を定義に追加
            string[] options = includeOptionString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (options.Length > 1 && options[0].Equals("define", StringComparison.OrdinalIgnoreCase))
            {
                localDefines.Add(options[1]);
            }
        }

        // インクルードファイルをラインオブジェクトとして読み込み
        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i];
            // テンプレート変数の置換
            foreach (var variable in variables)
            {
                line = line.Replace($"{{{{${variable.Key}}}}}", variable.Value);
            }

            var lineObject = new LineObject
            {
                StartLineNumber = i + 1,
                EndLineNumber = i + 1,
                OriginalStartLineNumber = i + 1,
                OriginalFilePath = includeFilePath,
                Lines = new List<string> { line }
            };
            includeLines.Add(lineObject);
        }

        // インクルードファイルに対しても条件付きコンパイルを適用
        try
        {
            includeLines = PreProcessConditionalCompile(includeLines, localDefines, Path.GetDirectoryName(includeFilePath), includeFilePath, variables);
        }
        catch (ParseException e)
        {
            // エラーメッセージにインクルードファイルの情報を追加
            throw new ParseException($"Error in included file {includeFilePath}: {e.Message}", e.LineObj);
        }

        return includeLines;
    }

    public List<LineObject> PreProcess(string filePath, HashSet<string> defines)
    {
        string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
        var processed = new List<LineObject>();
        var templateVariables = new Dictionary<string, string>();
        string currentProjectDirectoryFromRoot = Path.GetDirectoryName(filePath);
        string filePathAbs = Path.GetFullPath(filePath);

        for (int i = 0; i < lines.Length; i++)
        {
            var group = new LineObject
            {
                StartLineNumber = i + 1,
                OriginalStartLineNumber = i + 1,
                OriginalFilePath = filePath,
                Lines = new List<string> { lines[i] }
            };

            processed.Add(group);
        }

        // PreProcessConditionalCompileを呼び出す
        try
        {
            var conditionallyCompiled = PreProcessConditionalCompile(processed, defines, currentProjectDirectoryFromRoot, filePathAbs, templateVariables);
            return conditionallyCompiled;
        }
        catch (ParseException e)
        {
            Console.WriteLine($"Parse error: {e.Message} at line {e.LineObj.StartLineNumber} in file {e.LineObj.OriginalFilePath}");
            throw; // 再スロー
        }
    }

    public List<LineObject> PreProcessConditionalCompile(List<LineObject> lines, HashSet<string> defines, string currentProjectDirectoryFromRoot, string filePathAbs, Dictionary<string, string> templateVariables)
    {
        this.defines = new HashSet<string>(defines);
        var dstLines = new List<LineObject>();

        try
        {
            foreach (var lineObj in lines)
            {
                var line = lineObj.Lines[0];

                var commandMatch = Regex.Match(line, @"^@([a-zA-Z]+)(\s+(.+))?$");
                if (commandMatch.Success)
                {
                    var command = commandMatch.Groups[1].Value;
                    var option = commandMatch.Groups[3].Value;
                    ParseCommand(command, option, lineObj);
                    continue;
                }

                if (!CurrentCondition())
                {
                    continue;
                }

                var includeMatch = Regex.Match(line, @"^<<\[\s*(.+)\s*\]\s*(\((.+)?\))?$");
                if (includeMatch.Success)
                {
                    var includeLines = ParseInclude(includeMatch.Groups[1].Value, includeMatch.Groups[3].Value, currentProjectDirectoryFromRoot, filePathAbs, templateVariables, lineObj);
                    if (includeLines != null)
                    {
                        dstLines.AddRange(includeLines);
                        continue;
                    }
                }

                dstLines.Add(lineObj);
            }

            if (states.Count > 0)
            {
                var state = states.Peek();
                throw new ParseException($"Unclosed @if statement at line {state.LineObj.StartLineNumber} in file {state.LineObj.OriginalFilePath}", state.LineObj);
            }

            return dstLines;
        }
        catch (ParseException e)
        {
            Console.WriteLine($"Parse error: {e.Message} at line {e.LineObj.StartLineNumber} in file {e.LineObj.OriginalFilePath}");
            throw;
        }
    }
}
