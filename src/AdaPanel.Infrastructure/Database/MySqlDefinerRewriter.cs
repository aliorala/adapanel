using System.Text;

namespace AdaPanel.Infrastructure.Database;

/// <summary>Streaming, UTF-8 dump transformation. Never executes SQL or changes the source.</summary>
internal static class MySqlDefinerRewriter
{
    private static readonly HashSet<string> QuotedIdentifierModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ANSI_QUOTES", "ANSI", "DB2", "MAXDB", "MSSQL", "ORACLE", "POSTGRESQL"
    };

    internal static void EnsureSupportedSqlMode(string mode)
    {
        if (mode.Contains('\\') || mode.Split(',').Any(value => value.Trim().Equals("NO_BACKSLASH_ESCAPES", StringComparison.OrdinalIgnoreCase) || QuotedIdentifierModes.Contains(value.Trim())))
            throw new InvalidDataException("DEFINER adaptation does not support NO_BACKSLASH_ESCAPES, ANSI_QUOTES or compatibility modes that enable ANSI_QUOTES (ANSI, DB2, MAXDB, MSSQL, ORACLE, POSTGRESQL).");
    }

    public static Task<string?> FindObjectAtLineAsync(string sourcePath, long line, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var reader = new StreamReader(sourcePath, new UTF8Encoding(false, true), true, 16384);
        using var sink = new StreamWriter(Stream.Null);
        var lexer = new Lexer(reader, sink, cancellationToken);
        string? statement = null;
        string? kind = null;
        string? name = null;
        var qualifiedName = false;
        var nameComplete = false;
        long start = 1;
        while (lexer.Next() is { } token)
        {
            if (token.Kind == Kind.End)
            {
                if (start <= line && lexer.Line >= line)
                    return name == null ? statement : $"{statement} {kind} {name}";
                statement = kind = name = null;
                qualifiedName = false;
                nameComplete = false;
                continue;
            }
            if (statement == null)
            {
                start = lexer.TokenLine;
                if (start > line) return null;
                statement = token.Text.Length <= 64 ? token.Text : "SQL";
                if (token.Is("DELIMITER")) { lexer.ChangeDelimiter(); statement = null; continue; }
            }
            else if (kind == null && (token.Is("VIEW") || token.Is("TRIGGER") || token.Is("PROCEDURE") || token.Is("FUNCTION") || token.Is("EVENT") || token.Is("TABLE") || token.Is("INTO")))
                kind = token.Text;
            else if (kind != null && name == null && token.Kind is Kind.Word or Kind.Quoted && !token.Is("IF") && !token.Is("NOT") && !token.Is("EXISTS"))
                name = token.Written ? "[oversized identifier]" : token.Text[..Math.Min(token.Text.Length, 256)];
            else if (name != null && !nameComplete && token.Text == "." && name.Length <= 256)
                qualifiedName = true;
            else if (qualifiedName && !nameComplete && token.Kind is Kind.Word or Kind.Quoted)
            {
                name += "." + (token.Written ? "[oversized identifier]" : token.Text[..Math.Min(token.Text.Length, 256)]);
                qualifiedName = false;
                nameComplete = true;
            }
            else if (name != null) nameComplete = true;
        }
        return start <= line ? name == null ? statement : $"{statement} {kind} {name}" : null;
    }, cancellationToken);

    public static Task<long> RewriteAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default, string? initialSqlMode = null)
    {
        if (initialSqlMode != null) EnsureSupportedSqlMode(initialSqlMode);
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The transformed SQL must have a different path from the original backup.", nameof(targetPath));
        return Task.Run(() => Rewrite(sourcePath, targetPath, cancellationToken, initialSqlMode), cancellationToken);
    }

    private static long Rewrite(string sourcePath, string targetPath, CancellationToken cancellationToken, string? initialSqlMode)
    {
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bom = input.ReadByte() == 0xef && input.ReadByte() == 0xbb && input.ReadByte() == 0xbf;
        input.Position = bom ? 3 : 0;
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), false, 16384);
        using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(output, new UTF8Encoding(bom, true), 16384);
        var lexer = new Lexer(reader, writer, cancellationToken);
        var state = State.Start;
        var security = false;
        var sqlMode = false;
        var modeStage = 0;
        var restoredUnknownMode = false;
        var currentModeKnown = initialSqlMode != null;
        var conditionalSet = false;
        var knownSavedModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var saveStage = 0;
        var saveCandidateKnown = false;
        string? saveVariable = null;
        var adaptedPrefix = false;
        long changed = 0;
        void RememberSavedMode()
        {
            if (!saveCandidateKnown) return;
            if (knownSavedModes.Count >= 128 && !knownSavedModes.Contains(saveVariable!))
                throw new InvalidDataException("Too many saved SQL_MODE variables during DEFINER adaptation.");
            knownSavedModes.Add(saveVariable!);
        }
        while (lexer.Next() is { } token)
        {
            if (token.Kind == Kind.End)
            {
                if (saveStage == 8) RememberSavedMode();
                if (state is State.Equals or State.Account or State.Host)
                    throw new InvalidDataException("Incomplete DEFINER account before the SQL delimiter.");
                if (state is State.Prefix or State.AfterAccount && adaptedPrefix)
                    throw new InvalidDataException("CREATE DEFINER did not name a supported stored object.");
                lexer.Write(token);
                state = State.Start;
                security = sqlMode = false;
                conditionalSet = false;
                modeStage = 0;
                saveStage = 0;
                saveVariable = null;
                adaptedPrefix = false;
                continue;
            }
            if (state == State.AfterAccount)
            {
                if (token.Text == "@") { state = State.Host; continue; }
                state = State.Prefix;
            }
            if (state == State.Host)
            {
                RequireAccount(token);
                WriteLineBreaks(writer, token.Text);
                state = State.Prefix;
                continue;
            }
            if (state == State.Equals)
            {
                if (token.Text != "=") throw new InvalidDataException("Expected '=' after CREATE DEFINER.");
                lexer.Write(token);
                state = State.Account;
                continue;
            }
            if (state == State.Account)
            {
                RequireAccount(token);
                if (token.Is("CURRENT_USER"))
                {
                    lexer.Write(token);
                    state = State.Prefix;
                }
                else
                {
                    writer.Write("CURRENT_USER");
                    WriteLineBreaks(writer, token.Text);
                    state = State.AfterAccount;
                    changed++;
                    adaptedPrefix = true;
                }
                continue;
            }
            if (state == State.Start)
            {
                if (token.Is("DELIMITER"))
                {
                    lexer.Write(token);
                    lexer.ChangeDelimiter();
                    continue;
                }
                if (restoredUnknownMode && !token.Is("SET"))
                    throw new InvalidDataException("SQL after restoring a variable SQL_MODE cannot be safely adapted. Keep session-mode restoration at the end of the dump.");
                if (!token.Is("SET") && !token.Is("CREATE")) knownSavedModes.Clear();
                state = token.Is("CREATE") ? State.Prefix : token.Is("SET") ? State.Set : State.Body;
                if (state == State.Set) conditionalSet = lexer.ConditionalExecutableComment;
            }
            else if (state == State.Prefix)
            {
                if (token.Is("VIEW") || token.Is("TRIGGER") || token.Is("PROCEDURE") || token.Is("FUNCTION") || token.Is("EVENT"))
                    state = State.Body;
                else if (token.Is("DEFINER") && !security)
                    state = State.Equals;
                else if (token.Is("TABLE") || token.Is("DATABASE") || token.Is("SCHEMA") || token.Is("INDEX") || token.Is("USER") || token.Is("ROLE") || token.Is("SERVER"))
                {
                    if (adaptedPrefix) throw new InvalidDataException("DEFINER adaptation only supports views, triggers, procedures, functions and events.");
                    knownSavedModes.Clear();
                    state = State.Body;
                }
                security = token.Is("SECURITY");
            }
            else if (state == State.Set)
            {
                conditionalSet |= lexer.ConditionalExecutableComment;
                if ((token.Is("SQL_MODE") || token.Text.Equals("`SQL_MODE`", StringComparison.OrdinalIgnoreCase) || sqlMode) && conditionalSet)
                    throw new InvalidDataException("Conditional executable-comment SQL_MODE statements cannot be safely interpreted across MySQL and MariaDB versions during DEFINER adaptation.");
                // Track only the simple session-mode saves emitted by mysqldump.
                // The initial server mode is unknown; a validated literal makes it known.
                if (saveStage == 0)
                {
                    if (token.Text == "@") saveStage = 1;
                }
                else if (saveStage == 1)
                {
                    if (token.Kind == Kind.Word && token.Text.Length <= 128) { saveVariable = token.Text; saveStage = 2; }
                    else saveStage = 0;
                }
                else if (saveStage == 2)
                {
                    if (token.Text == "=") { knownSavedModes.Remove(saveVariable!); saveStage = 3; }
                    else if (token.Text == ":") { knownSavedModes.Remove(saveVariable!); saveStage = 0; }
                    else saveStage = 0;
                }
                else if (saveStage == 3) saveStage = token.Text == "@" ? 4 : 0;
                else if (saveStage == 4) saveStage = token.Text == "@" ? 5 : 0;
                else if (saveStage == 5 || saveStage == 7)
                {
                    if (token.Is("SQL_MODE"))
                    {
                        saveCandidateKnown = currentModeKnown;
                        saveStage = 8;
                    }
                    else saveStage = saveStage == 5 && (token.Is("SESSION") || token.Is("LOCAL")) ? 6 : 0;
                }
                else if (saveStage == 6) saveStage = token.Text == "." ? 7 : 0;
                else if (saveStage == 8)
                {
                    if (token.Text == ",") RememberSavedMode();
                    saveStage = 0;
                }
                sqlMode |= token.Is("SQL_MODE") || token.Text.Equals("`SQL_MODE`", StringComparison.OrdinalIgnoreCase);
                if (token.Written)
                    throw new InvalidDataException("Oversized SET statement token cannot be safely interpreted during DEFINER adaptation.");
                if (sqlMode && token.Text.Contains("NO_BACKSLASH_ESCAPES", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("DEFINER adaptation does not support SQL_MODE=NO_BACKSLASH_ESCAPES. Restore with original owners or prepare a compatible SQL export.");
                if (sqlMode && token.Kind == Kind.Quoted && token.Text.Length >= 2 &&
                    token.Text[1..^1].Split(',').Any(mode => QuotedIdentifierModes.Contains(mode.Trim())))
                    throw new InvalidDataException("DEFINER adaptation does not support SQL_MODE=ANSI_QUOTES or compatibility modes that enable it (ANSI, DB2, MAXDB, MSSQL, ORACLE, POSTGRESQL). Restore with original owners or prepare a compatible SQL export.");
                if (modeStage == 1)
                {
                    if (token.Text == ":") throw new InvalidDataException("SQL_MODE ':=' assignments cannot be safely interpreted during DEFINER adaptation.");
                    modeStage = token.Text == "=" ? 2 : 0;
                }
                else if (modeStage == 2)
                {
                    if (token.Kind == Kind.Quoted && token.Text[0] is '\'' or '"')
                    {
                        EnsureSupportedSqlMode(token.Text[1..^1]);
                        restoredUnknownMode = false;
                        currentModeKnown = true;
                        modeStage = 3;
                    }
                    else if (token.Text == "@") { modeStage = 4; restoredUnknownMode = true; }
                    else throw new InvalidDataException("Dynamic or encoded SQL_MODE assignments cannot be safely interpreted during DEFINER adaptation.");
                }
                else if (modeStage == 3)
                {
                    if (token.Text != ",") throw new InvalidDataException("SQL_MODE expressions cannot be safely interpreted during DEFINER adaptation.");
                    modeStage = 0;
                }
                else if (modeStage == 4)
                {
                    if (token.Kind != Kind.Word) throw new InvalidDataException("Unsupported variable SQL_MODE restoration.");
                    currentModeKnown = knownSavedModes.Contains(token.Text);
                    restoredUnknownMode = !currentModeKnown;
                    modeStage = 3;
                }
                else if (token.Is("SQL_MODE") || token.Text.Equals("`SQL_MODE`", StringComparison.OrdinalIgnoreCase))
                    modeStage = 1;
            }
            lexer.Write(token);
        }
        if (state is State.Equals or State.Account or State.Host)
            throw new InvalidDataException("The SQL dump ends with an incomplete DEFINER account.");
        if (state is State.Prefix or State.AfterAccount && adaptedPrefix)
            throw new InvalidDataException("CREATE DEFINER did not name a supported stored object.");
        writer.Flush();
        return changed;
    }

    private static void RequireAccount(Token token)
    {
        if (token.Kind is not (Kind.Word or Kind.Quoted) || token.Written)
            throw new InvalidDataException("Unsupported or oversized DEFINER account in SQL dump.");
    }

    private static void WriteLineBreaks(TextWriter writer, string text)
    {
        foreach (var c in text) if (c is '\r' or '\n') writer.Write(c);
    }

    private enum State { Start, Prefix, Equals, Account, AfterAccount, Host, Set, Body }
    private enum Kind { Word, Quoted, Symbol, End }
    private sealed record Token(Kind Kind, string Text, bool Written = false)
    {
        public bool Is(string value) => Kind == Kind.Word && Text.Equals(value, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Lexer(StreamReader reader, StreamWriter writer, CancellationToken cancellationToken)
    {
        private const int MaxToken = 65536;
        private readonly List<int> lookahead = [];
        private string delimiter = ";";
        private bool executableComment;
        public bool ConditionalExecutableComment { get; private set; }
        private long consumed;
        public long Line { get; private set; } = 1;
        public long TokenLine { get; private set; } = 1;

        private int Peek(int offset = 0)
        {
            while (lookahead.Count <= offset) lookahead.Add(reader.Read());
            return lookahead[offset];
        }

        private int Read()
        {
            if ((++consumed & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = Peek();
            lookahead.RemoveAt(0);
            if (value == 0) throw new InvalidDataException("NUL bytes are not supported in a UTF-8 SQL dump.");
            if (value == '\n') Line++;
            return value;
        }

        private void Copy() => writer.Write((char)Read());
        private bool Matches(string value)
        {
            for (var i = 0; i < value.Length; i++) if (Peek(i) != value[i]) return false;
            return true;
        }

        public void Write(Token token) { if (!token.Written) writer.Write(token.Text); }

        public void ChangeDelimiter()
        {
            var line = new StringBuilder();
            while (Peek() is not (-1 or '\r' or '\n'))
            {
                if (line.Length > 128) throw new InvalidDataException("Unsupported DELIMITER command.");
                var c = (char)Read();
                line.Append(c);
                writer.Write(c);
            }
            var value = line.ToString().Trim();
            if (value.Length is < 1 or > 16 || value.Any(char.IsWhiteSpace) || value.Any(char.IsLetterOrDigit) || value.Contains('#') || value.Contains("--", StringComparison.Ordinal) || value.Contains("/*", StringComparison.Ordinal) || value.Contains("*/", StringComparison.Ordinal) || value.Contains('\'') || value.Contains('"') || value.Contains('`') || value.Contains('\\'))
                throw new InvalidDataException("Unsupported SQL delimiter.");
            delimiter = value;
        }

        public Token? Next()
        {
            while (true)
            {
                var c = Peek();
                if (c < 0)
                {
                    if (executableComment) throw new InvalidDataException("Unterminated executable SQL comment.");
                    return null;
                }
                if (char.IsWhiteSpace((char)c)) { Copy(); continue; }
                if (executableComment && Matches("*/")) { Copy(); Copy(); executableComment = false; ConditionalExecutableComment = false; continue; }
                if (Matches("/*"))
                {
                    if (executableComment) throw new InvalidDataException("Nested executable SQL comments are not supported.");
                    // MariaDB's dump header is a client sandbox directive, not a SQL
                    // statement. It has no delimiter and must not consume CREATE's state.
                    const string sandboxHeader = "/*M!999999\\- enable the sandbox mode */";
                    const string lowerSandboxHeader = "/*m!999999\\- enable the sandbox mode */";
                    if (Matches(sandboxHeader) || Matches(lowerSandboxHeader))
                    {
                        for (var i = 0; i < sandboxHeader.Length; i++) Copy();
                        continue;
                    }
                    Copy(); Copy();
                    if (Peek() == '!' || ((Peek() == 'M' || Peek() == 'm') && Peek(1) == '!'))
                    {
                        var mariaOnly = Peek() != '!';
                        if (Peek() != '!') Copy();
                        Copy();
                        executableComment = true;
                        var version = 0;
                        var versionDigits = 0;
                        while (char.IsAsciiDigit((char)Peek()))
                        {
                            if (++versionDigits > 6) throw new InvalidDataException("Unsupported executable SQL comment version.");
                            version = version * 10 + Peek() - '0';
                            Copy();
                        }
                        ConditionalExecutableComment = mariaOnly || version >= 50700;
                        while (Peek() >= 0 && char.IsWhiteSpace((char)Peek())) Copy();
                        if (Peek() == '\\')
                            throw new InvalidDataException("Unsupported or malformed executable SQL client directive.");
                    }
                    else
                    {
                        while (!Matches("*/"))
                        {
                            if (Peek() < 0) throw new InvalidDataException("Unterminated SQL block comment.");
                            Copy();
                        }
                        Copy(); Copy();
                    }
                    continue;
                }
                if (c == '#' || (Matches("--") && (Peek(2) < 0 || char.IsWhiteSpace((char)Peek(2)))))
                {
                    while (Peek() is not (-1 or '\r' or '\n')) Copy();
                    continue;
                }
                if (Matches(delimiter))
                {
                    TokenLine = Line;
                    for (var i = 0; i < delimiter.Length; i++) Read();
                    return new Token(Kind.End, delimiter);
                }
                TokenLine = Line;
                if (c is '\'' or '"' or '`') return Quoted((char)c);
                if (char.IsLetterOrDigit((char)c) || c is '_' or '$')
                    return Word();
                return new Token(Kind.Symbol, ((char)Read()).ToString());
            }
        }

        private Token Word()
        {
            var text = new StringBuilder();
            var streamed = false;
            while (Peek() >= 0 && (char.IsLetterOrDigit((char)Peek()) || Peek() is '_' or '$'))
            {
                // A custom delimiter (e.g. $$) can touch an identifier at statement end.
                if (Matches(delimiter)) break;
                Append(text, (char)Read(), ref streamed);
            }
            return Finish(Kind.Word, text, streamed);
        }

        private Token Quoted(char quote)
        {
            var text = new StringBuilder();
            var streamed = false;
            Append(text, (char)Read(), ref streamed);
            while (true)
            {
                if (Peek() < 0) throw new InvalidDataException("Unterminated quoted SQL value.");
                var c = (char)Read();
                Append(text, c, ref streamed);
                if (c == '\\' && quote != '`')
                {
                    if (Peek() < 0) throw new InvalidDataException("Unterminated SQL escape sequence.");
                    Append(text, (char)Read(), ref streamed);
                }
                else if (c == quote)
                {
                    if (Peek() != quote) break;
                    Append(text, (char)Read(), ref streamed);
                }
            }
            return Finish(Kind.Quoted, text, streamed);
        }

        private void Append(StringBuilder text, char c, ref bool streamed)
        {
            if (!streamed && text.Length == MaxToken)
            {
                writer.Write(text.ToString());
                text.Clear();
                streamed = true;
            }
            if (streamed) writer.Write(c); else text.Append(c);
        }

        private Token Finish(Kind kind, StringBuilder text, bool streamed) => new(kind, streamed ? "" : text.ToString(), streamed);
    }
}
