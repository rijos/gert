using FluentAssertions;
using Gert.Service.Validation;
using Gert.Testing.TestData;
using Xunit;

namespace Gert.Service.Tests.Validation;

/// <summary>
/// Unit tests for the shared <see cref="ValidationRules"/> predicates - the DRY
/// building blocks every validator leans on, so they get direct coverage including
/// the adversarial corpus (testing.md section 5). Non-printable / look-alike chars
/// are built from numeric code points so the source stays plain ASCII.
/// </summary>
public sealed class ValidationRulesTests
{
    private const char Esc = (char)0x001B;
    private const char Del = (char)0x007F;
    private const char Nel = (char)0x0085;          // C1 control
    private const char Rlo = (char)0x202E;          // RIGHT-TO-LEFT OVERRIDE
    private const char Rlm = (char)0x200F;          // RIGHT-TO-LEFT MARK
    private const char Lri = (char)0x2066;          // LEFT-TO-RIGHT ISOLATE
    private const char Pdi = (char)0x2069;          // POP DIRECTIONAL ISOLATE
    private const char CyrillicA = (char)0x0430;    // Cyrillic small 'a' homoglyph

    [Theory]
    [InlineData("hello world", false)]
    [InlineData("multi\nline\ttext\r", false)] // tab/newline/CR are allowed
    [InlineData("abc\0def", true)] // NUL
    [InlineData("bell\achar", true)] // BEL
    [InlineData("plain-ascii", false)]
    public void ContainsForbiddenControlChar_flags_only_dangerous_controls(string input, bool expected) =>
        ValidationRules.ContainsForbiddenControlChar(input).Should().Be(expected);

    [Fact]
    public void ContainsForbiddenControlChar_flags_c0_c1_and_del()
    {
        ValidationRules.ContainsForbiddenControlChar("esc" + Esc + "x").Should().BeTrue();
        ValidationRules.ContainsForbiddenControlChar("del" + Del).Should().BeTrue();
        ValidationRules.ContainsForbiddenControlChar("c1" + Nel).Should().BeTrue();
    }

    [Fact]
    public void ContainsBidiOverride_flags_directional_formatting()
    {
        ValidationRules.ContainsBidiOverride("plain").Should().BeFalse();
        ValidationRules.ContainsBidiOverride("user" + Rlo + "gpj.exe").Should().BeTrue();
        ValidationRules.ContainsBidiOverride(Rlm + "mark").Should().BeTrue();
        ValidationRules.ContainsBidiOverride(Lri + "iso" + Pdi).Should().BeTrue();
    }

    [Theory]
    [InlineData("doc.pdf", true)]
    [InlineData("a really long name with spaces.docx", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("foo/bar.txt", false)]
    [InlineData("foo\\bar.txt", false)]
    [InlineData("..", false)]
    [InlineData("with\0null.txt", false)]
    [InlineData("/abs/path.txt", false)]
    [InlineData("C:\\win.txt", false)]
    [InlineData("", false)]
    public void IsSafeFilename_rejects_traversal_and_separators(string input, bool expected) =>
        ValidationRules.IsSafeFilename(input).Should().Be(expected);

    [Theory]
    [MemberData(nameof(TraversalRows))]
    public void IsSafeFilename_rejects_every_traversal_payload(string payload) =>
        ValidationRules.IsSafeFilename(payload).Should().BeFalse();

    public static IEnumerable<object[]> TraversalRows() =>
        NaughtyStrings.TheoryData(NaughtyStrings.PathTraversal);

    [Theory]
    [InlineData("00000000000000000000000000000000000000000000000000000000000000ff", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", true)]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", false)] // upper rejected
    [InlineData("../etc", false)]
    [InlineData("short", false)]
    [InlineData("g000000000000000000000000000000000000000000000000000000000000000", false)] // non-hex
    public void IsWellFormedAdminKey_matches_sha256_hex_only(string input, bool expected) =>
        ValidationRules.IsWellFormedAdminKey(input).Should().Be(expected);

    [Fact]
    public void IsWellFormedAdminKey_rejects_wrong_length()
    {
        ValidationRules.IsWellFormedAdminKey(new string('a', 63)).Should().BeFalse();
        ValidationRules.IsWellFormedAdminKey(new string('a', 65)).Should().BeFalse();
        ValidationRules.IsWellFormedAdminKey(new string('a', 64)).Should().BeTrue();
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("not-a-guid", false)]
    [InlineData("../escape", false)]
    public void IsWellFormedProjectId_accepts_default_or_guid(string input, bool expected) =>
        ValidationRules.IsWellFormedProjectId(input).Should().Be(expected);

    [Fact]
    public void IsWellFormedProjectId_accepts_a_real_guid() =>
        ValidationRules.IsWellFormedProjectId(Guid.NewGuid().ToString()).Should().BeTrue();

    [Fact]
    public void IsWellFormedId_is_pinned_to_the_canonical_D_format_the_storage_guards_require()
    {
        // The storage guards (StorageKeys/SqliteDatabasePaths.ValidatePid) require
        // TryParseExact("D"); any other Guid format must 400 here, not 500 there.
        var guid = Guid.NewGuid();

        ValidationRules.IsWellFormedId(guid.ToString("D")).Should().BeTrue();
        ValidationRules.IsWellFormedId(guid.ToString("N")).Should().BeFalse("no-dash format");
        ValidationRules.IsWellFormedId(guid.ToString("B")).Should().BeFalse("braced format");
        ValidationRules.IsWellFormedId(guid.ToString("P")).Should().BeFalse("parenthesised format");
        ValidationRules.IsWellFormedId(guid.ToString("X")).Should().BeFalse("hex-grouped format");
    }

    [Fact]
    public void IsWellFormedProjectId_rejects_non_D_guid_formats()
    {
        var guid = Guid.NewGuid();

        ValidationRules.IsWellFormedProjectId(guid.ToString("N")).Should().BeFalse();
        ValidationRules.IsWellFormedProjectId(guid.ToString("B")).Should().BeFalse();
        ValidationRules.IsWellFormedProjectId(guid.ToString("P")).Should().BeFalse();
    }

    [Theory]
    [InlineData("qwen2.5:7b", true)]
    [InlineData("llama-3.1-8b-instruct", true)]
    [InlineData("model with space", false)]
    [InlineData("", false)]
    public void IsSafeIdentifier_accepts_conservative_charset(string input, bool expected) =>
        ValidationRules.IsSafeIdentifier(input).Should().Be(expected);

    [Fact]
    public void IsSafeIdentifier_rejects_homoglyphs() =>
        ValidationRules.IsSafeIdentifier(CyrillicA + "dmin").Should().BeFalse();

    [Theory]
    [InlineData("doc.pdf", "pdf")]
    [InlineData("DOC.PDF", "pdf")]
    [InlineData("archive.tar.gz", "gz")]
    [InlineData("noext", "")]
    public void ExtensionOf_lowercases(string input, string expected) =>
        ValidationRules.ExtensionOf(input).Should().Be(expected);
}
