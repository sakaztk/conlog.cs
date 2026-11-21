using System;
using System.IO;
using System.Text;

namespace ConLog;

/// <summary>
/// Minimal terminal emulator. It interprets escape sequences to reproduce the screen
/// and writes text out when a line scrolls off the screen, when the screen is cleared,
/// and on exit. The log stays readable even for shells such as PSReadLine that redraw
/// the line in place with cursor movements.
/// </summary>
sealed class ScreenLog : ILog
{
    enum S { Normal, Esc, Csi, Osc, OscEsc, EscSkip }

    readonly object sync = new object();
    readonly StreamWriter w;
    readonly bool ts;
    readonly Decoder[] dec;         // one decoder per input channel (stdout / stderr)
    readonly Encoding src;
    readonly bool crlf;             // pipe mode: a bare LF also returns the cursor to column 0
    readonly StringBuilder csi = new StringBuilder();
    readonly StringBuilder carry = new StringBuilder();   // joins wrapped rows into one logical line
    DateTime carryTime;
    bool carryActive;

    int cols, rows, cx, cy, top, bottom, savedX, savedY;
    int[][] cells = null!;          // code points; 0 = empty, -1 = second half of a wide character
    bool[] cont = null!;            // row continues the previous row (auto-wrapped)
    DateTime[] rowTime = null!;     // time the first character was written to the row
    bool wrapPending, alt;
    char high;
    S s = S.Normal;

    public ScreenLog(string path, bool append, bool timestamp, int cols, int rows,
                     Encoding logEnc, Encoding srcEnc, bool newlineImpliesCr)
    {
        var fs = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        w = new StreamWriter(fs, logEnc);
        ts = timestamp;
        src = srcEnc;
        dec = new[] { srcEnc.GetDecoder(), srcEnc.GetDecoder() };
        crlf = newlineImpliesCr;
        Init(Math.Max(cols, 1), Math.Max(rows, 1));
    }

    void Init(int c, int r)
    {
        cols = c; rows = r; cx = cy = 0; top = 0; bottom = r - 1;
        cells = new int[r][];
        cont = new bool[r];
        rowTime = new DateTime[r];
        for (int i = 0; i < r; i++) cells[i] = new int[c];
    }

    // ---------------- ILog ----------------

    public void Write(byte[] buf, int n, int channel = 0)
    {
        var chars = new char[src.GetMaxCharCount(n)];
        lock (sync)
        {
            int c = dec[channel & 1].GetChars(buf, 0, n, chars, 0, false);
            for (int i = 0; i < c; i++) Feed(chars[i]);
        }
    }

    /// <summary>Writes a header/footer line. Whatever is on the screen is written out first.</summary>
    public void WriteMeta(string line)
    {
        lock (sync)
        {
            DumpScreen();
            ClearAll();
            w.WriteLine(line);
            w.Flush();
        }
    }

    public void Resize(int c, int r)
    {
        lock (sync)
        {
            c = Math.Max(c, 1); r = Math.Max(r, 1);
            if (c == cols && r == rows) return;

            while (rows > r)
            {
                if (cy > 0) { ScrollUp(0, rows - 1, 1, true); cy--; }   // push the top row out to the log
                rows--;
                Array.Resize(ref cells, rows);
                Array.Resize(ref cont, rows);
                Array.Resize(ref rowTime, rows);
            }
            while (rows < r)
            {
                rows++;
                Array.Resize(ref cells, rows);
                cells[rows - 1] = new int[cols];
                Array.Resize(ref cont, rows);
                Array.Resize(ref rowTime, rows);
            }
            if (c != cols)
                for (int i = 0; i < rows; i++) Array.Resize(ref cells[i], c);

            cols = c;
            top = 0; bottom = rows - 1;
            cx = Math.Min(cx, cols - 1);
            cy = Math.Min(cy, rows - 1);
            wrapPending = false;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            DumpScreen();
            w.Dispose();
        }
    }

    // ---------------- Input interpretation ----------------

    void Feed(char ch)
    {
        switch (s)
        {
            case S.Esc: EscChar(ch); return;
            case S.Csi:
                if (ch >= '@' && ch <= '~') { s = S.Normal; Dispatch(ch); }
                else if (ch == '\x1b') s = S.Esc;
                else if (csi.Length < 64) csi.Append(ch);
                return;
            case S.Osc:
                if (ch == '\a') s = S.Normal;
                else if (ch == '\x1b') s = S.OscEsc;
                return;
            case S.OscEsc:
            case S.EscSkip:
                s = S.Normal;
                return;
        }

        // S.Normal
        if (ch == '\x1b') { s = S.Esc; return; }
        if (alt) return;   // do not record while the alternate screen (vim, less, ...) is active

        switch (ch)
        {
            case '\r': cx = 0; wrapPending = false; return;
            case '\n': case '\v': case '\f': wrapPending = false; if (crlf) cx = 0; LineFeed(); return;
            case '\b': if (cx > 0) cx--; wrapPending = false; return;
            case '\t': cx = Math.Min(cols - 1, (cx / 8 + 1) * 8); wrapPending = false; return;
        }
        if (ch < ' ' || ch == '\x7f') return;

        int cp = ch;
        if (char.IsHighSurrogate(ch)) { high = ch; return; }
        if (char.IsLowSurrogate(ch))
        {
            if (high == 0) return;
            cp = char.ConvertToUtf32(high, ch);
            high = (char)0;
        }
        Put(cp);
    }

    void EscChar(char ch)
    {
        s = S.Normal;
        switch (ch)
        {
            case '[': csi.Clear(); s = S.Csi; break;
            case ']': case 'P': case '^': case '_': case 'X': s = S.Osc; break;
            case '(': case ')': case '*': case '+': case '#': case '%': case ' ': s = S.EscSkip; break;
            case '7': if (!alt) { savedX = cx; savedY = cy; } break;
            case '8': if (!alt) { cx = Math.Min(savedX, cols - 1); cy = Math.Min(savedY, rows - 1); wrapPending = false; } break;
            case 'M':
                if (alt) break;
                if (cy == top) ScrollDown(top, bottom, 1);
                else if (cy > 0) cy--;
                break;
            case 'D': if (!alt) LineFeed(); break;
            case 'E': if (!alt) { cx = 0; LineFeed(); } break;
            case 'c': if (!alt) { DumpScreen(); ClearAll(); cx = cy = 0; top = 0; bottom = rows - 1; } break;
        }
    }

    void Dispatch(char fin)
    {
        string p = csi.ToString();
        foreach (char c in p) if (c >= ' ' && c <= '/') return;   // ignore sequences with intermediate bytes (e.g. cursor shape)

        bool priv = p.Length > 0 && (p[0] == '?' || p[0] == '>' || p[0] == '<' || p[0] == '=');
        bool isDec = priv && p[0] == '?';
        if (priv) p = p.Substring(1);
        int[] a = ParseParams(p);
        int N(int i, int def) { return i < a.Length && a[i] > 0 ? a[i] : def; }

        if (isDec && (fin == 'h' || fin == 'l'))
        {
            foreach (int v in a)
                if (v == 1049 || v == 47 || v == 1047) alt = fin == 'h';
            return;
        }
        if (priv || alt) return;

        wrapPending = false;
        switch (fin)
        {
            case 'A': cy = Math.Max(0, cy - N(0, 1)); break;
            case 'B': cy = Math.Min(rows - 1, cy + N(0, 1)); break;
            case 'C': cx = Math.Min(cols - 1, cx + N(0, 1)); break;
            case 'D': cx = Math.Max(0, cx - N(0, 1)); break;
            case 'E': cx = 0; cy = Math.Min(rows - 1, cy + N(0, 1)); break;
            case 'F': cx = 0; cy = Math.Max(0, cy - N(0, 1)); break;
            case 'G': case '`': cx = Math.Min(cols, N(0, 1)) - 1; break;
            case 'd': cy = Math.Min(rows, N(0, 1)) - 1; break;
            case 'H':
            case 'f':
                cy = Math.Min(rows, N(0, 1)) - 1;
                cx = Math.Min(cols, N(1, 1)) - 1;
                break;
            case 'J':
                {
                    int m = a.Length > 0 ? a[0] : 0;
                    if (m == 2 || (m == 0 && cx == 0 && cy == 0)) { DumpScreen(); ClearAll(); }
                    else if (m == 0)
                    {
                        ClearCells(cy, cx, cols);
                        for (int r = cy + 1; r < rows; r++) ClearCells(r, 0, cols);
                    }
                    else if (m == 1)
                    {
                        for (int r = 0; r < cy; r++) ClearCells(r, 0, cols);
                        ClearCells(cy, 0, cx + 1);
                    }
                    break;
                }
            case 'K':
                {
                    int m = a.Length > 0 ? a[0] : 0;
                    if (m == 0) ClearCells(cy, cx, cols);
                    else if (m == 1) ClearCells(cy, 0, cx + 1);
                    else if (m == 2) ClearCells(cy, 0, cols);
                    break;
                }
            case 'X': ClearCells(cy, cx, cx + N(0, 1)); break;
            case 'P':   // DCH: delete characters, shifting the rest of the row left
                {
                    int n = Math.Min(N(0, 1), cols - cx);
                    var row = cells[cy];
                    Array.Copy(row, cx + n, row, cx, cols - cx - n);
                    Array.Clear(row, cols - n, n);
                    break;
                }
            case '@':   // ICH: insert blanks, shifting the rest of the row right
                {
                    int n = Math.Min(N(0, 1), cols - cx);
                    var row = cells[cy];
                    Array.Copy(row, cx, row, cx + n, cols - cx - n);
                    Array.Clear(row, cx, n);
                    break;
                }
            case 'L': if (cy >= top && cy <= bottom) ScrollDown(cy, bottom, N(0, 1)); break;
            case 'M': if (cy >= top && cy <= bottom) ScrollUp(cy, bottom, N(0, 1), false); break;
            case 'S': ScrollUp(top, bottom, N(0, 1), top == 0); break;
            case 'T': ScrollDown(top, bottom, N(0, 1)); break;
            case 'r':
                {
                    int t = N(0, 1) - 1;
                    int b = (a.Length > 1 && a[1] > 0 ? a[1] : rows) - 1;
                    if (t < b && b < rows) { top = t; bottom = b; } else { top = 0; bottom = rows - 1; }
                    cx = 0; cy = 0;
                    break;
                }
            case 's': savedX = cx; savedY = cy; break;
            case 'u': cx = Math.Min(savedX, cols - 1); cy = Math.Min(savedY, rows - 1); break;
                // everything else (e.g. 'm' for colors) is ignored
        }
    }

    static int[] ParseParams(string p)
    {
        if (p.Length == 0) return new int[0];
        var parts = p.Split(';', ':');
        var r = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            int v;
            if (!int.TryParse(parts[i], out v)) v = 0;
            r[i] = v;
        }
        return r;
    }

    // ---------------- Screen operations ----------------

    void Put(int cp)
    {
        int wd = Width(cp);
        if (wd == 0) return;
        if (wd > cols) wd = cols;

        if (wrapPending || cx + wd > cols)
        {
            cx = 0;
            wrapPending = false;
            LineFeed();
            cont[cy] = true;
        }

        var row = cells[cy];
        if (row[cx] == -1 && cx > 0) row[cx - 1] = ' ';
        if (cx + 1 < cols && row[cx + 1] == -1) row[cx + 1] = 0;

        if (rowTime[cy] == default(DateTime)) rowTime[cy] = DateTime.Now;
        row[cx] = cp;
        if (wd == 2) row[cx + 1] = -1;
        cx += wd;
        if (cx >= cols) { cx = cols - 1; wrapPending = true; }
    }

    void LineFeed()
    {
        if (cy == bottom) ScrollUp(top, bottom, 1, top == 0);
        else if (cy < rows - 1) cy++;
    }

    void ScrollUp(int t, int b, int n, bool emit)
    {
        n = Math.Min(n, b - t + 1);
        for (int k = 0; k < n; k++)
        {
            if (emit) EmitRow(t);
            var first = cells[t];
            for (int i = t; i < b; i++)
            {
                cells[i] = cells[i + 1];
                cont[i] = cont[i + 1];
                rowTime[i] = rowTime[i + 1];
            }
            Array.Clear(first, 0, first.Length);
            cells[b] = first;
            cont[b] = false;
            rowTime[b] = default(DateTime);
        }
    }

    void ScrollDown(int t, int b, int n)
    {
        n = Math.Min(n, b - t + 1);
        for (int k = 0; k < n; k++)
        {
            var last = cells[b];
            for (int i = b; i > t; i--)
            {
                cells[i] = cells[i - 1];
                cont[i] = cont[i - 1];
                rowTime[i] = rowTime[i - 1];
            }
            Array.Clear(last, 0, last.Length);
            cells[t] = last;
            cont[t] = false;
            rowTime[t] = default(DateTime);
        }
    }

    void ClearCells(int r, int from, int to)
    {
        from = Math.Max(0, from);
        to = Math.Min(cols, to);
        for (int i = from; i < to; i++) cells[r][i] = 0;
        if (from == 0 && to >= cols) { cont[r] = false; rowTime[r] = default(DateTime); }
    }

    void ClearAll()
    {
        for (int r = 0; r < rows; r++) ClearCells(r, 0, cols);
    }

    // ---------------- Log output ----------------

    bool IsBlank(int r)
    {
        foreach (int c in cells[r]) if (c > 0) return false;
        return true;
    }

    string RowText(int r, bool trim)
    {
        var sb = new StringBuilder(cols);
        foreach (int cp in cells[r])
        {
            if (cp == -1) continue;
            if (cp == 0) sb.Append(' ');
            else sb.Append(char.ConvertFromUtf32(cp));
        }
        return trim ? sb.ToString().TrimEnd(' ') : sb.ToString();
    }

    /// <summary>Writes one row to the log. Wrapped rows are joined with the following rows first.</summary>
    void EmitRow(int r)
    {
        if (!carryActive) { carryTime = rowTime[r]; carryActive = true; }
        bool more = r + 1 < rows && cont[r + 1];
        carry.Append(RowText(r, !more));
        if (!more) FlushCarry();
    }

    void FlushCarry()
    {
        if (!carryActive) return;
        if (ts)
        {
            var t = carryTime == default(DateTime) ? DateTime.Now : carryTime;
            w.Write(t.ToString("yyyy-MM-dd HH:mm:ss.fff "));
        }
        w.WriteLine(carry.ToString());
        w.Flush();
        carry.Clear();
        carryTime = default(DateTime);
        carryActive = false;
    }

    /// <summary>Writes the current screen to the log (leading and trailing blank rows are omitted).</summary>
    void DumpScreen()
    {
        int first = 0, last = rows - 1;
        while (first <= last && IsBlank(first)) first++;
        while (last >= first && IsBlank(last)) last--;
        for (int r = first; r <= last; r++) EmitRow(r);
        FlushCarry();
    }

    static int Width(int c)
    {
        if (c >= 0x0300 && c <= 0x036F) return 0;
        if (c == 0x200B || c == 0x200D || (c >= 0xFE00 && c <= 0xFE0F)) return 0;
        if (c >= 0x1100 &&
            (c <= 0x115F || c == 0x2329 || c == 0x232A ||
             (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) ||
             (c >= 0xAC00 && c <= 0xD7A3) ||
             (c >= 0xF900 && c <= 0xFAFF) ||
             (c >= 0xFE30 && c <= 0xFE6F) ||
             (c >= 0xFF00 && c <= 0xFF60) ||
             (c >= 0xFFE0 && c <= 0xFFE6) ||
             (c >= 0x1F300 && c <= 0x1F64F) ||
             (c >= 0x1F900 && c <= 0x1F9FF) ||
             (c >= 0x20000 && c <= 0x3FFFD)))
            return 2;
        return 1;
    }
}