using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    /// <summary>
    /// Guards the model pickup's own language tables (Localization/Languages/*.json). Every language
    /// must carry exactly en.json's keys and keep en's format placeholders and rich-text tags.
    /// BasisLocalizationCore.Format returns the raw template when string.Format throws, so a broken
    /// translation would otherwise show "{0}" in the UI instead of failing anywhere.
    ///
    /// Engine-free: the files are found relative to this source file through [CallerFilePath], which is
    /// absolute under dotnet and project-relative under Unity (whose working directory is the project root).
    /// </summary>
    public sealed class BasisModelLocalizationKeyTests
    {
        private static readonly string[] Codes =
        {
            "ar", "bn", "de", "en", "es-MX", "es", "fr", "hi", "it", "ja", "nl", "pt", "ru", "ur", "zh-Hans", "zh-Hant",
        };

        // Every key the runtime looks up.
        private static readonly string[] RequiredKeys =
        {
            "modelPickup.popup.rejected.title",
            "modelPickup.popup.limit.title",
            "modelPickup.popup.accept",
            "modelPickup.popup.unknownFile",
            "modelPickup.popup.defaultReason",
            "modelPickup.popup.rejection.description",
            "modelPickup.popup.limit.summary",
            "modelPickup.popup.limit.allowedSome",
            "modelPickup.popup.limit.noneImported",
            "modelPickup.popup.reason.adminLocked",
            "modelPickup.popup.reason.adminLockedDuringLoad",
            "modelPickup.popup.reason.noPermission",
            "modelPickup.popup.reason.textureRejected",
            "modelPickup.popup.reason.importFailed",
            "modelPickup.popup.reason.memoryBudget",
            "modelPickup.size.title",
            "modelPickup.size.description",
            "modelPickup.size.fit",
            "modelPickup.size.original",
            "modelPickup.panel.spawnedLocally",
            "modelPickup.panel.spawnedBy",
            "modelPickup.panel.hide",
            "modelPickup.panel.show",
            "modelPickup.panel.save",
            "modelPickup.panel.delete",
            "modelPickup.panel.confirm",
            "modelPickup.shareable.loading",
            "modelPickup.shareable.detail",
        };

        private const string SizeDescriptionKey = "modelPickup.size.description";
        private const string SizeFitKey = "modelPickup.size.fit";
        private const string SizeOriginalKey = "modelPickup.size.original";

        private static readonly Regex Placeholder = new Regex(@"\{(\d+)(?:,-?\d+)?(?::[^{}]*)?\}", RegexOptions.CultureInvariant);
        private static readonly Regex Tag = new Regex(@"<[^<>]+>", RegexOptions.CultureInvariant);
        private static readonly Regex Bold = new Regex("<b>(.*?)</b>", RegexOptions.CultureInvariant | RegexOptions.Singleline);

        private static readonly Dictionary<string, LanguageTable> Tables = new Dictionary<string, LanguageTable>(StringComparer.Ordinal);

        private sealed class LanguageTable
        {
            public string Code;
            public string NativeName;
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly List<string> DuplicateKeys = new List<string>();
        }

        [Test]
        public void LanguageFolderHoldsExactlyTheBasisLanguages()
        {
            string directory = LanguagesDirectory();
            Assert.That(Directory.Exists(directory), Is.True, "Language folder not found: " + directory);

            List<string> found = new List<string>();
            foreach (string path in Directory.GetFiles(directory, "*.json"))
            {
                found.Add(Path.GetFileNameWithoutExtension(path));
            }

            Assert.That(found, Is.EquivalentTo(Codes));
        }

        [Test]
        public void EnglishDefinesEveryKeyTheRuntimeUses()
        {
            LanguageTable en = Load("en");
            List<string> missing = new List<string>();
            foreach (string key in RequiredKeys)
            {
                if (!en.Values.ContainsKey(key)) missing.Add(key);
            }

            Assert.That(missing, Is.Empty, "en.json lacks: " + string.Join(", ", missing));
        }

        [TestCaseSource(nameof(Codes))]
        public void FileDeclaresItsCodeAndHasNoDuplicateOrEmptyEntries(string code)
        {
            LanguageTable table = Load(code);

            Assert.That(table.Code, Is.EqualTo(code), code + ".json declares a different code.");
            Assert.That(table.NativeName, Is.Not.Null.And.Not.Empty, code + ".json has no nativeName.");
            Assert.That(table.DuplicateKeys, Is.Empty, code + ".json repeats: " + string.Join(", ", table.DuplicateKeys));

            List<string> empty = new List<string>();
            foreach (KeyValuePair<string, string> pair in table.Values)
            {
                if (pair.Value.Trim().Length == 0) empty.Add(pair.Key);
            }

            Assert.That(empty, Is.Empty, code + ".json has empty values for: " + string.Join(", ", empty));
        }

        [TestCaseSource(nameof(Codes))]
        public void KeySetMatchesEnglish(string code)
        {
            LanguageTable en = Load("en");
            LanguageTable table = Load(code);
            List<string> missing = new List<string>();
            List<string> extra = new List<string>();

            foreach (string key in en.Values.Keys)
            {
                if (!table.Values.ContainsKey(key)) missing.Add(key);
            }

            foreach (string key in table.Values.Keys)
            {
                if (!en.Values.ContainsKey(key)) extra.Add(key);
            }

            Assert.That(missing, Is.Empty, code + ".json lacks: " + string.Join(", ", missing));
            Assert.That(extra, Is.Empty, code + ".json has keys en.json does not: " + string.Join(", ", extra));
        }

        [TestCaseSource(nameof(Codes))]
        public void PlaceholdersMatchEnglish(string code)
        {
            LanguageTable en = Load("en");
            LanguageTable table = Load(code);
            List<string> mismatches = new List<string>();

            foreach (KeyValuePair<string, string> pair in en.Values)
            {
                if (!table.Values.TryGetValue(pair.Key, out string translated)) continue;

                string expected = string.Join(" ", Placeholders(pair.Value));
                string actual = string.Join(" ", Placeholders(translated));
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    mismatches.Add(pair.Key + ": expected [" + expected + "] but found [" + actual + "]");
                }
            }

            Assert.That(mismatches, Is.Empty, code + ".json:\n" + string.Join("\n", mismatches));
        }

        [TestCaseSource(nameof(Codes))]
        public void ValuesFormatWithTheEnglishArgumentCount(string code)
        {
            LanguageTable en = Load("en");
            LanguageTable table = Load(code);
            List<string> failures = new List<string>();

            foreach (KeyValuePair<string, string> pair in en.Values)
            {
                int argumentCount = ArgumentCount(pair.Value);
                if (argumentCount == 0 || !table.Values.TryGetValue(pair.Key, out string translated)) continue;

                // Integers satisfy both plain "{n}" and "{n:N0}" items.
                object[] args = new object[argumentCount];
                for (int i = 0; i < argumentCount; i++) args[i] = 12345 + i;

                try
                {
                    string.Format(CultureInfo.InvariantCulture, translated, args);
                }
                catch (FormatException e)
                {
                    failures.Add(pair.Key + ": " + e.Message);
                }
            }

            Assert.That(failures, Is.Empty, code + ".json:\n" + string.Join("\n", failures));
        }

        [TestCaseSource(nameof(Codes))]
        public void RichTextTagsMatchEnglish(string code)
        {
            LanguageTable en = Load("en");
            LanguageTable table = Load(code);
            List<string> mismatches = new List<string>();

            foreach (KeyValuePair<string, string> pair in en.Values)
            {
                if (!table.Values.TryGetValue(pair.Key, out string translated)) continue;

                string expected = string.Join("", Tags(pair.Value));
                string actual = string.Join("", Tags(translated));
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    mismatches.Add(pair.Key + ": expected " + expected + " but found " + actual);
                }
            }

            Assert.That(mismatches, Is.Empty, code + ".json:\n" + string.Join("\n", mismatches));
        }

        // The dialog's accept button is Fit and its deny button is Original size (lead decision L2), and
        // the dialogue panel lays accept out first. The description must name both buttons, in that order,
        // with the words the buttons actually show.
        [TestCaseSource(nameof(Codes))]
        public void SizeDescriptionNamesTheButtonsInButtonOrder(string code)
        {
            LanguageTable table = Load(code);
            Assert.That(table.Values.TryGetValue(SizeDescriptionKey, out string description), Is.True, code + ".json lacks " + SizeDescriptionKey);
            Assert.That(table.Values.TryGetValue(SizeFitKey, out string fit), Is.True, code + ".json lacks " + SizeFitKey);
            Assert.That(table.Values.TryGetValue(SizeOriginalKey, out string original), Is.True, code + ".json lacks " + SizeOriginalKey);

            MatchCollection bold = Bold.Matches(description);
            Assert.That(bold.Count, Is.EqualTo(2), code + ".json: the size description should bold exactly the two button names.");

            string fitName = bold[0].Groups[1].Value;
            string originalName = bold[1].Groups[1].Value;
            Assert.That(fitName.Length, Is.GreaterThan(0));
            Assert.That(fit.StartsWith(fitName, StringComparison.Ordinal), Is.True,
                code + ".json: the first bold name \"" + fitName + "\" is not how the Fit button \"" + fit + "\" begins.");
            Assert.That(originalName, Is.EqualTo(original),
                code + ".json: the second bold name does not match the Original size button.");
        }

        private static List<string> Placeholders(string value)
        {
            List<string> found = new List<string>();
            foreach (Match match in Placeholder.Matches(StripEscapedBraces(value))) found.Add(match.Value);
            found.Sort(StringComparer.Ordinal);
            return found;
        }

        private static int ArgumentCount(string value)
        {
            int count = 0;
            foreach (Match match in Placeholder.Matches(StripEscapedBraces(value)))
            {
                int index = int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                if (index + 1 > count) count = index + 1;
            }

            return count;
        }

        private static List<string> Tags(string value)
        {
            List<string> found = new List<string>();
            foreach (Match match in Tag.Matches(value)) found.Add(match.Value);
            return found;
        }

        private static string StripEscapedBraces(string value)
        {
            return value.Replace("{{", string.Empty).Replace("}}", string.Empty);
        }

        private static LanguageTable Load(string code)
        {
            if (Tables.TryGetValue(code, out LanguageTable cached)) return cached;

            string path = Path.Combine(LanguagesDirectory(), code + ".json");
            Assert.That(File.Exists(path), Is.True, "Missing language file: " + path);

            Dictionary<string, object> root = MiniJson.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            Assert.That(root, Is.Not.Null, code + ".json: the root is not an object.");

            LanguageTable table = new LanguageTable
            {
                Code = root.TryGetValue("code", out object codeValue) ? codeValue as string : null,
                NativeName = root.TryGetValue("nativeName", out object nameValue) ? nameValue as string : null,
            };

            List<object> entries = root.TryGetValue("entries", out object entriesValue) ? entriesValue as List<object> : null;
            Assert.That(entries, Is.Not.Null, code + ".json has no entries array.");

            for (int i = 0; i < entries.Count; i++)
            {
                Dictionary<string, object> entry = entries[i] as Dictionary<string, object>;
                Assert.That(entry, Is.Not.Null, code + ".json: entry " + i + " is not an object.");

                string key = entry.TryGetValue("key", out object keyValue) ? keyValue as string : null;
                string value = entry.TryGetValue("value", out object valueValue) ? valueValue as string : null;
                Assert.That(key, Is.Not.Null.And.Not.Empty, code + ".json: entry " + i + " has no key.");
                Assert.That(value, Is.Not.Null, code + ".json: " + key + " has no string value.");

                if (table.Values.ContainsKey(key)) table.DuplicateKeys.Add(key);
                else table.Values.Add(key, value);
            }

            Tables[code] = table;
            return table;
        }

        private static string LanguagesDirectory([CallerFilePath] string sourceFile = "")
        {
            // <package>/Tests/Editor/EngineFree/<this file>  ->  <package>/Localization/Languages
            string here = Path.GetDirectoryName(sourceFile);
            return Path.GetFullPath(Path.Combine(here, "..", "..", "..", "Localization", "Languages"));
        }

        /// <summary>
        /// Just enough JSON for the language files: objects become Dictionary&lt;string, object&gt;,
        /// arrays List&lt;object&gt;, numbers double. Unity's test assembly has no JSON library that
        /// is free of UnityEngine, so this keeps the test engine-free.
        /// </summary>
        private sealed class MiniJson
        {
            private readonly string _text;
            private int _index;

            private MiniJson(string text)
            {
                _text = text;
            }

            public static object Parse(string text)
            {
                MiniJson reader = new MiniJson(text);
                reader.SkipWhitespace();
                object value = reader.ReadValue();
                reader.SkipWhitespace();
                if (reader._index != reader._text.Length) throw reader.Error("has trailing characters");
                return value;
            }

            private object ReadValue()
            {
                if (_index >= _text.Length) throw Error("ends early");

                switch (_text[_index])
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': ExpectLiteral("true"); return true;
                    case 'f': ExpectLiteral("false"); return false;
                    case 'n': ExpectLiteral("null"); return null;
                    default: return ReadNumber();
                }
            }

            private Dictionary<string, object> ReadObject()
            {
                Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
                _index++;
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _index++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw Error("expects a string key");
                    string key = ReadString();
                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    result[key] = ReadValue();
                    SkipWhitespace();
                    if (Peek() == ',')
                    {
                        _index++;
                        continue;
                    }

                    Expect('}');
                    return result;
                }
            }

            private List<object> ReadArray()
            {
                List<object> result = new List<object>();
                _index++;
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _index++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    result.Add(ReadValue());
                    SkipWhitespace();
                    if (Peek() == ',')
                    {
                        _index++;
                        continue;
                    }

                    Expect(']');
                    return result;
                }
            }

            private string ReadString()
            {
                StringBuilder builder = new StringBuilder();
                _index++;
                while (true)
                {
                    if (_index >= _text.Length) throw Error("has an unterminated string");
                    char c = _text[_index++];
                    if (c == '"') return builder.ToString();
                    if (c < ' ') throw Error("has a raw control character in a string");
                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }

                    if (_index >= _text.Length) throw Error("has an unterminated escape");
                    char escape = _text[_index++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (_index + 4 > _text.Length) throw Error("has a short \\u escape");
                            builder.Append((char)int.Parse(_text.Substring(_index, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                            _index += 4;
                            break;
                        default: throw Error("has an unknown escape");
                    }
                }
            }

            private double ReadNumber()
            {
                int start = _index;
                while (_index < _text.Length && "+-0123456789.eE".IndexOf(_text[_index]) >= 0) _index++;
                if (_index == start) throw Error("has an unexpected character");
                return double.Parse(_text.Substring(start, _index - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private void ExpectLiteral(string literal)
            {
                if (string.CompareOrdinal(_text, _index, literal, 0, literal.Length) != 0) throw Error("has an unknown literal");
                _index += literal.Length;
            }

            private void Expect(char c)
            {
                if (Peek() != c) throw Error("expects '" + c + "'");
                _index++;
            }

            private char Peek()
            {
                return _index < _text.Length ? _text[_index] : '\0';
            }

            private void SkipWhitespace()
            {
                while (_index < _text.Length)
                {
                    char c = _text[_index];
                    if (c != ' ' && c != '\t' && c != '\r' && c != '\n') return;
                    _index++;
                }
            }

            private FormatException Error(string problem)
            {
                return new FormatException("JSON " + problem + " at offset " + _index + ".");
            }
        }
    }
}
