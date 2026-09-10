using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HuTao.Agent.Core.DocumentReading;

/// <summary>当前支持 TXT 和 DOCX；后续格式只需新增 IDocumentTextExtractor 实现。</summary>
public sealed class DocumentTextExtractor : IDocumentTextExtractor
{
    public IReadOnlyList<string> Extract(string path)
        => Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase)
            ? ExtractDocx(path)
            : ExtractTxt(path);

    private static IReadOnlyList<string> ExtractTxt(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var encoding = bytes is [0xFF, 0xFE, ..] ? Encoding.Unicode :
            bytes is [0xFE, 0xFF, ..] ? Encoding.BigEndianUnicode :
            bytes is [0xEF, 0xBB, 0xBF, ..] ? new UTF8Encoding(true) :
            new UTF8Encoding(false, true);
        try
        {
            return SplitParagraphs(encoding.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return SplitParagraphs(Encoding.Default.GetString(bytes));
        }
    }

    private static IReadOnlyList<string> ExtractDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml")
                     ?? throw new InvalidDataException("DOCX 缺少 word/document.xml。");
        using var stream = entry.Open();
        var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return document.Descendants(w + "p")
            .Select(paragraph => string.Concat(
                paragraph.Descendants(w + "t").Select(text => text.Value)))
            .Select(text => text.Trim())
            .Where(text => text.Length > 0)
            .ToList();
    }

    private static IReadOnlyList<string> SplitParagraphs(string text)
        => Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n|\n")
            .Select(line => Regex.Replace(line, @"[ \t]+", " ").Trim().TrimStart('\uFEFF'))
            .Where(line => line.Length > 0)
            .ToList();
}
