using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using GTranslate.Results;
using GTranslate.Translators;

public partial class Program {

    static string DotnetPathName = $@"{Environment.ExpandEnvironmentVariables(@"%PROGRAMFILES%")}\dotnet\packs";
    static string PackagesPathName = $@"{Environment.ExpandEnvironmentVariables(@"%USERPROFILE%")}\.nuget\packages";

    static ConcurrentDictionary<string, string> TranslateCache = [];
    
    static int TranslationTotal;
    static int TranslationFail;
    static object nodeLock = new object();
    static ITranslator translator = new BingTranslator();
    static ITranslator translator2 = new GoogleTranslator2();
    static void Main(string[] args) {
        try {
            Console.WriteLine("加载翻译缓存..");
            foreach (string line in File.ReadAllLines("./TranslateCache.txt")) {
                if (line.Split("|") is [var original, var translate])
                    TranslateCache.TryAdd(original.Replace("&#124;", "|"), translate.Replace("&#124;", "|"));
            }
          
        }
        catch (Exception) {

        }
        //翻译官方库
        //foreach (var pack in Directory.EnumerateDirectories(DotnetPathName, "*.Ref.*")) {
        //    Other.Out(pack);
        //    var maxVersionPath = Directory.EnumerateDirectories(pack).Max();//只翻译最大版本
        //    var refPath = Path.Combine(maxVersionPath, "ref");
        //    if (Directory.Exists(refPath)) {
        //        foreach (var dri in Directory.EnumerateDirectories(refPath)) {
        //            foreach (var pathName in Directory.GetFiles(dri, "*.xml")) {
        //                var outputName = Path.Combine(Path.GetDirectoryName(pathName),"zh-hans", Path.GetFileName(pathName));
        //                if (!File.Exists(outputName)) {
        //                    Console.WriteLine($"翻译:{Path.GetFileName(pack)}/../{Path.GetFileName(pathName)} ...");
        //                    TranslateXml(pathName, outputName.Replace(DotnetPathName, @".\Outs\packs"));
        //                }
        //            }
        //        }
        //    }
        //}
        //翻译第三方包
        foreach (var pack in Directory.EnumerateDirectories(PackagesPathName)) {
            Debug.WriteLine(pack);
            var maxVersionPath = Directory.EnumerateDirectories(pack).Max();//只翻译最大版本
            var libPath = Path.Combine(maxVersionPath, "lib");
            if (Directory.Exists(libPath)) {
                foreach (var framework in Directory.EnumerateDirectories(libPath)) {
                    foreach (var pathName in Directory.GetFiles(framework, "*.xml")) {
                        var outputName = Path.Combine(Path.GetDirectoryName(pathName), "zh-hans", Path.GetFileName(pathName));
                        //if (!File.Exists(outputName)) {
                            Console.WriteLine($"翻译:{pathName} ...");
                            TranslateXml(pathName, outputName.Replace(PackagesPathName, @".\Outs\packages"));
                        //}
                    }

                }
            }
        }

        Console.WriteLine("翻译完成..");
        Console.ReadKey();
    }


    public static void TranslateXml(string pathName,string outputName) {
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.DtdProcessing = DtdProcessing.Parse;
        try {
            XmlDocument xml = new XmlDocument();
            xml.Load(pathName);
            if (xml.GetElementsByTagName("doc").Item(0) is XmlNode doc) {
                if (doc.SelectNodes("members").Item(0).Cast<XmlNode>().ToArray() is XmlNode[] members) {
                    int parallelCount = 0;
                    int completedCount = 0;
                    foreach (XmlNode member in members) {
                        if (Interlocked.Increment(ref parallelCount) > 200) {
                            while (parallelCount > 200)
                                Task.Delay(50).Wait();
                        }
                        Task.Run(async () => {
                            try {
                                await ReplaceMember(member);
                            }
                            finally {
                                int count = Interlocked.Increment(ref completedCount);

                                Console.Title = $"({members.Length}/{count}-[{(double)count / members.Length * 100:f2}%]), 翻译:({TranslationTotal}; 失败:{TranslationFail}-{(double)TranslationFail / TranslationTotal * 100:f2}%)";
                                Interlocked.Decrement(ref parallelCount);
                            }
                        });
                    }


                    while (parallelCount > 0)
                        Task.Delay(50).Wait();

                }
            }
            if (!Directory.Exists(Path.GetDirectoryName(outputName)))
                Directory.CreateDirectory(Path.GetDirectoryName(outputName));
            xml.Save(outputName);

        }
        catch (Exception ex) {
            Console.WriteLine(ex.Message);
        }
    }

    static async Task ReplaceMember(XmlNode node) {
        foreach (XmlNode item in node.Cast<XmlNode>().ToList()) {
            if (item.NodeType == XmlNodeType.Comment)
                continue;
            if (item.NodeType == XmlNodeType.Element && item.Name == "appledoc")
                continue;
            var xml = await ReplaceNode(item);
            try {
                lock (nodeLock)
                    item.InnerXml = xml;
            }
            catch (Exception ex) {}
        }
    }

    static Dictionary<string, TaskCompletionSource<string>> Translation = [];
    static async Task<string> ReplaceNode(XmlNode node) {
        Dictionary<uint, string> reserveMap = [];
        StringBuilder stringBuilder = new StringBuilder();
        bool isTranslator = false;
        foreach (XmlNode su in node.ChildNodes.Cast<XmlNode>().ToList()) {
            switch (su.NodeType) {
                case XmlNodeType.Text: {
                    isTranslator = true;
                    string outerXml;
                    lock (nodeLock)
                        outerXml = su.OuterXml;
                    if (stringBuilder.Length > 0 && stringBuilder[^1] != ' ')
                        stringBuilder.Append(' ');
                    stringBuilder.Append(outerXml.Trim());
                    break;
                }
                case XmlNodeType.Element: {
                    switch (su.Name) {
                        case "see":
                        case "seealso":
                        case "altmember":
                        case "c":
                        case "i":
                        case "paramref":
                        case "typeparamref":
                        case "appledoc":
                        case "img":
                        case "code": {
                            string outerXml;
                            lock (nodeLock) 
                                outerXml = su.OuterXml;
                            uint hash = StableHash(outerXml);
                            reserveMap[hash] = outerXml;
                            if (stringBuilder.Length > 0 && stringBuilder[^1] != ' ')
                                stringBuilder.Append(' ');
                            stringBuilder.Append($"__{hash}__");
                            break;
                        }
                        case "summary":
                        case "remarks":
                        case "returns":
                        case "param":
                        case "list":
                        case "item":
                        case "description":
                        case "exception":
                        case "block":
                        case "para":
                        case "term":
                        case "format":
                        default: {
                            string innerXml = await ReplaceNode(su);
                            string outerXml;
                            lock (nodeLock) {
                                su.InnerXml = innerXml;
                                outerXml = su.OuterXml;
                            }
                            uint hash = StableHash(outerXml);
                            reserveMap[hash] = outerXml; 
                            if (stringBuilder.Length > 0 && stringBuilder[^1] != ' ')
                                stringBuilder.Append(' ');
                            stringBuilder.Append($"__{hash}__");
                            break;
                        }
                    }
                    break;
                }
                case XmlNodeType.Whitespace: break;
                case XmlNodeType.CDATA: break;
                default: break;
            }
        }

        //isTranslator = false;
        string original = NewlineRegex().Replace(stringBuilder.ToString(), (m) => " ").Trim().Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&").Replace("&quot;", "\"").Replace("&apos;", "\'");
        string translate = "";
        if (isTranslator && !string.IsNullOrEmpty(original)) {
            if (!TranslateCache.TryGetValue(original, out translate)) {
                TaskCompletionSource<string> taskCompletion;
                bool isTranslation = false;
                lock (Translation) {
                    isTranslation = Translation.TryGetValue(original, out taskCompletion);
                    if (!isTranslation) {
                        taskCompletion = new TaskCompletionSource<string>();
                        Translation.Add(original, taskCompletion);
                    }
                }
                if (isTranslation) {
                    translate = await taskCompletion.Task;
                }
                else {
                    int reCount = 0;
                    Interlocked.Increment(ref TranslationTotal);
                    re:
                    try {
                        ITranslationResult result;
                        if (original.Length < 1000) {
                            result = await translator.TranslateAsync(original, "zh-cn");
                        }
                        else {
                            result = await translator2.TranslateAsync(original, "zh-cn");
                        }
                        translate = result?.Translation;
                        if (string.IsNullOrWhiteSpace(translate))
                            throw new Exception("返回空");
                        translate = EscapeRegex().Replace(translate, (m) => {
                            return m.Value switch {
                                "，" => ", ",
                                "。" => ". ",
                                "（" => " (",
                                "）" => ") ",
                                "“" => "&quot;",
                                "”" => "&quot;",
                                "：：" => "::",
                                "：" => ": ",

                                "&" => "&amp;",
                                "<" => "&lt;",
                                ">" => "&gt;",
                                "\"" => "&quot;",
                                "'" => "&apos;",
                                _ => m.Value
                            };
                        });

                    }
                    catch (Exception ex) {
                        if (reCount++ < 3)
                            goto re;
                        Debug.WriteLine(ex);
                        Debug.WriteLine(original);
                        Interlocked.Increment(ref TranslationFail);
                        translate = original;
                    }
                    if (TranslateCache.TryAdd(original, translate)) {
                        lock (TranslateCache)
                            File.AppendAllTextAsync("./TranslateCache.txt", $"{original.Replace("|", "&#124;")}|{translate.Replace("|", "&#124;")}\r\n");
                    }
                    if (original.Length > 1000) {
                        Debug.WriteLine(original);
                        Debug.WriteLine(translate);
                    }
                        taskCompletion.TrySetResult(translate);
                    lock (Translation) 
                        Translation.Remove(original);
                }
            }
        }
        else {
            translate = original;
        }
        translate = ReserveRegex().Replace(translate, (m) => {
            if (reserveMap.TryGetValue(uint.Parse(m.Groups[1].Value), out var eText))
                return eText;
            return m.Value;
        });
        return translate.Trim();
    }
    public static uint StableHash(string input) {
        unchecked {
            uint hash = 23;
            foreach (var c in input)
                hash = hash * 31 + c;
            return hash;
        }
    }





    [GeneratedRegex(@"__([\d]+)__",RegexOptions.IgnoreCase)]
    private static partial Regex ReserveRegex();
    [GeneratedRegex(@"\s*(\n|\r\n)\s*")]
    private static partial Regex NewlineRegex();
    [GeneratedRegex("，|。|（|）|“|”|：：|：|<|>|\"|'|&")]
    private static partial Regex EscapeRegex();
}