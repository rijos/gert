namespace Gert.Testing.TestData;

/// <summary>
/// The adversarial input corpus (testing.md section 5 threat table). One shared set fed
/// through <b>every</b> string field via xUnit <c>[Theory]</c>/<c>[MemberData]</c>:
/// every input must be <b>rejected or safely accepted - never crash, never slip
/// through</b> to persistence. Grouped by threat so a validator test can target a
/// category, or run <see cref="All"/> across the board.
/// </summary>
/// <remarks>
/// Non-printable and look-alike characters are constructed from their Unicode code
/// points via <see cref="char"/> casts (rather than literal bytes or escape
/// sequences) so the source file stays plain ASCII and the exact code point is
/// self-documenting.
/// </remarks>
public static class NaughtyStrings
{
    private static readonly string Nul = ((char)0x0000).ToString();          // NULL
    private static readonly string Bell = ((char)0x0007).ToString();         // BELL
    private static readonly string VerticalTab = ((char)0x000B).ToString();  // VERTICAL TAB
    private static readonly string FormFeed = ((char)0x000C).ToString();     // FORM FEED
    private static readonly string Esc = ((char)0x001B).ToString();          // ESCAPE (ANSI)
    private static readonly string RtlOverride = ((char)0x202E).ToString();  // RIGHT-TO-LEFT OVERRIDE
    private static readonly string Rlm = ((char)0x200F).ToString();          // RIGHT-TO-LEFT MARK
    private static readonly string Lri = ((char)0x2066).ToString();          // LEFT-TO-RIGHT ISOLATE
    private static readonly string Pdi = ((char)0x2069).ToString();          // POP DIRECTIONAL ISOLATE
    private static readonly string ZeroWidthSpace = ((char)0x200B).ToString(); // ZERO WIDTH SPACE
    private static readonly string Nbsp = ((char)0x00A0).ToString();         // NO-BREAK SPACE

    private static readonly string CyrillicA = ((char)0x0430).ToString();    // Cyrillic small 'a'
    private static readonly string UnicodeHyphen = ((char)0x2010).ToString(); // HYPHEN (not ASCII '-')
    private static readonly string GreekOmicron = ((char)0x03BF).ToString(); // Greek small omicron
    private static readonly string RomanFifty = ((char)0x217C).ToString();   // small roman numeral fifty

    /// <summary>Path-traversal / separator-injection payloads (filenames, ids, admin keys).</summary>
    public static readonly IReadOnlyList<string> PathTraversal =
    [
        "../etc/passwd",
        "..\\..\\windows\\system32",
        "../../../../../../etc/shadow",
        "foo/../../bar",
        "/etc/passwd",
        "C:\\Windows\\System32\\config\\SAM",
        "....//....//etc/passwd",
        "%2e%2e%2fetc%2fpasswd",
        "..%c0%afetc",
        "doc/../../../rag.db",
    ];

    /// <summary>Null bytes and control characters (truncation, log injection, parser confusion).</summary>
    public static readonly IReadOnlyList<string> ControlChars =
    [
        "abc" + Nul + "def",
        Nul + "leading-null",
        "trailing-null" + Nul,
        "bell" + Bell + "char",
        "tab\tand\nnewline\r",
        "vertical" + VerticalTab + "tab",
        Esc + "[31mansi-escape" + Esc + "[0m",
        "form" + FormFeed + "feed",
    ];

    /// <summary>Bidi-override and zero-width characters (display spoofing, RTL trickery).</summary>
    public static readonly IReadOnlyList<string> BidiAndZeroWidth =
    [
        "user" + RtlOverride + "gpj.exe",
        RtlOverride + "reversed",
        "zero" + ZeroWidthSpace + "width" + ZeroWidthSpace + "space",
        Rlm + "right-to-left-mark",
        Lri + "isolate" + Pdi,
    ];

    /// <summary>Oversized blobs (DoS via huge payloads).</summary>
    public static readonly IReadOnlyList<string> Oversized =
    [
        new string('A', 10_000),
        new string('x', 100_000),
        new string((char)0x5B57, 50_000), // CJK ideograph repeated
        string.Concat(Enumerable.Repeat("LongWord ", 20_000)),
    ];

    /// <summary>SQL / FTS5 metacharacters (injection / query-syntax abuse - carried as data, not operators).</summary>
    public static readonly IReadOnlyList<string> SqlAndFts =
    [
        "'; DROP TABLE documents; --",
        "\" OR \"1\"=\"1",
        "1; DELETE FROM chunks WHERE 1=1; --",
        "foo* AND bar NEAR/3 baz",
        "title:\"injected\" OR rowid > 0",
        "%' UNION SELECT * FROM sqlite_master --",
        "MATCH 'a' OR 'b'",
        "NULL) ; ATTACH DATABASE '/tmp/evil.db' AS evil; --",
    ];

    /// <summary>HTML / script payloads (stored XSS if rendered unescaped).</summary>
    public static readonly IReadOnlyList<string> HtmlAndScript =
    [
        "<script>alert(1)</script>",
        "<img src=x onerror=alert(1)>",
        "<svg/onload=alert(1)>",
        "javascript:alert(document.cookie)",
        "</textarea><script>fetch('//evil')</script>",
        "<iframe src='http://169.254.169.254/'></iframe>",
        "&lt;already-escaped&gt;",
        "<a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>",
    ];

    /// <summary>Homoglyphs / lookalike unicode (identity/model-id spoofing).</summary>
    public static readonly IReadOnlyList<string> Homoglyphs =
    [
        CyrillicA + "dmin",                  // Cyrillic 'a' (U+0430), not Latin 'a'
        "gert" + UnicodeHyphen + "api",      // U+2010 hyphen vs ASCII '-'
        RomanFifty + "llama",                // U+217C small roman numeral fifty
        "m" + GreekOmicron + "del",          // Greek omicron in "model"
    ];

    /// <summary>SSRF / scheme-abuse URLs (web-search fetch, link rewriting).</summary>
    public static readonly IReadOnlyList<string> SsrfUrls =
    [
        "http://169.254.169.254/latest/meta-data/",
        "http://127.0.0.1:8080/admin",
        "http://localhost/healthz",
        "http://[::1]/",
        "http://10.0.0.1/internal",
        "http://192.168.1.1/router",
        "file:///etc/passwd",
        "gopher://evil/_",
        "ftp://internal/secret",
        "http://0177.0.0.1/",               // octal-encoded loopback
    ];

    /// <summary>Empty / whitespace-only inputs (reject null/whitespace-only fields).</summary>
    public static readonly IReadOnlyList<string> Emptyish =
    [
        string.Empty,
        " ",
        "   \t  ",
        "\n\r\n",
        Nbsp,
        ZeroWidthSpace,
    ];

    /// <summary>Every category concatenated - the full adversarial corpus.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        .. PathTraversal,
        .. ControlChars,
        .. BidiAndZeroWidth,
        .. Oversized,
        .. SqlAndFts,
        .. HtmlAndScript,
        .. Homoglyphs,
        .. SsrfUrls,
        .. Emptyish,
    ];

    /// <summary>
    /// xUnit <c>[MemberData]</c> source: every string in <see cref="All"/> as a
    /// single-arg theory row.
    /// </summary>
    public static IEnumerable<object[]> AllTheoryData() => All.Select(s => new object[] { s });

    /// <summary>xUnit <c>[MemberData]</c> source over a specific category.</summary>
    public static IEnumerable<object[]> TheoryData(IReadOnlyList<string> category) =>
        category.Select(s => new object[] { s });
}
