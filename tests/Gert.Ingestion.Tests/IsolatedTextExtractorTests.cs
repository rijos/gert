using System.Xml;
using FluentAssertions;
using Gert.Ingestion;
using Gert.Ingestion.Subprocess;
using Gert.Service.Ingestion;
using Xunit;

namespace Gert.Ingestion.Tests;

/// <summary>
/// Unit tests for the isolated PDF/DOCX/XLSX extractor's pure surfaces (security F7): the
/// command/arg builder caps, the XML-hardening flags, the zip-bomb guard, and the
/// helper-output -> graceful-result mapping. No subprocess.
/// </summary>
public sealed class IsolatedTextExtractorTests
{
    [Theory]
    [InlineData("pdf", true)]
    [InlineData("docx", true)]
    [InlineData("xlsx", true)]
    [InlineData("txt", false)]
    [InlineData("md", false)]
    [InlineData("json", false)]
    public void CanExtract_BinaryDocumentFormatsOnly(string ext, bool expected)
    {
        var extractor = new IsolatedTextExtractor(
            Microsoft.Extensions.Options.Options.Create(new ExtractorOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IsolatedTextExtractor>.Instance);
        extractor.CanExtract(ext).Should().Be(expected);
    }

    [Fact]
    public void BuildArgs_CarriesCapsUidAndNoNetwork()
    {
        var options = new ExtractorParameters
        {
            RunAsUid = 65534,
            AddressSpaceMiB = 512,
            CpuSeconds = 20,
            ProcessLimit = 16,
            MaxDecompressedBytes = 64L * 1024 * 1024,
            MaxZipEntries = 2048,
        };

        var args = ExtractorCommandBuilder.BuildArgs(options, "pdf", "/tmp/in.pdf");

        args.Should().ContainInConsecutiveOrder("--type", "pdf");
        args.Should().ContainInConsecutiveOrder("--input", "/tmp/in.pdf");
        args.Should().ContainInConsecutiveOrder("--uid", "65534");
        args.Should().ContainInConsecutiveOrder("--rlimit-as-mib", "512");
        args.Should().ContainInConsecutiveOrder("--rlimit-cpu", "20");
        args.Should().ContainInConsecutiveOrder("--rlimit-nproc", "16");
        args.Should().Contain("--no-network");
    }

    [Fact]
    public void HardenedXml_DisablesDtdAndExternalEntities()
    {
        var settings = HardenedXml.CreateSafeSettings();

        // XmlResolver is set-only; the null-resolver behaviour is verified end-to-end by
        // HardenedXml_RejectsDocTypeXxe below. Here we assert the readable hardening flags.
        settings.DtdProcessing.Should().Be(DtdProcessing.Prohibit);
        settings.MaxCharactersFromEntities.Should().BeGreaterThan(0);
    }

    [Fact]
    public void HardenedXml_RejectsDocTypeXxe()
    {
        const string xxe = """
        <?xml version="1.0"?>
        <!DOCTYPE foo [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
        <root>&xxe;</root>
        """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xxe));
        using var reader = HardenedXml.CreateReader(stream);

        var act = () =>
        {
            while (reader.Read())
            {
            }
        };

        act.Should().Throw<XmlException>("DTDs are prohibited so the DOCTYPE is rejected");
    }

    [Fact]
    public void ZipBombGuard_TripsOnEntryCount()
    {
        var guard = new ZipBombGuard(maxDecompressedBytes: 1_000_000, maxEntries: 2);

        guard.TryAccountEntry(10).Should().BeTrue();
        guard.TryAccountEntry(10).Should().BeTrue();
        guard.TryAccountEntry(10).Should().BeFalse();
        guard.Tripped.Should().BeTrue();
        guard.TripReason.Should().Contain("entry count");
    }

    [Fact]
    public void ZipBombGuard_TripsOnDecompressedSize()
    {
        var guard = new ZipBombGuard(maxDecompressedBytes: 100, maxEntries: 100);

        guard.TryAccountEntry(60).Should().BeTrue();
        guard.TryAccountEntry(60).Should().BeFalse();
        guard.TripReason.Should().Contain("Decompressed size");
    }

    [Fact]
    public void ParseHelperOutput_Success_MapsPages()
    {
        var json = """{ "pages": [ { "text": "page one", "locator": "p.1" }, { "text": "page two" } ] }""";
        var result = IsolatedTextExtractor.ParseHelperOutput(0, json, string.Empty);

        result.Error.Should().BeNull();
        result.Pages.Should().HaveCount(2);
        result.Pages[0].Text.Should().Be("page one");
        result.Pages[0].Locator.Should().Be("p.1");
        result.Pages[1].Locator.Should().BeNull();
        result.HasText.Should().BeTrue();
    }

    [Fact]
    public void ParseHelperOutput_NonZeroExit_Fails()
    {
        var result = IsolatedTextExtractor.ParseHelperOutput(139, string.Empty, "segfault");
        result.Error.Should().NotBeNull();
        result.Error.Should().Contain("segfault");
        result.Pages.Should().BeEmpty();
    }

    [Fact]
    public void ParseHelperOutput_GarbageOutput_Fails()
    {
        var result = IsolatedTextExtractor.ParseHelperOutput(0, "not json", string.Empty);
        result.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task ExtractAsync_HelperUnavailable_FailsGracefully()
    {
        var extractor = new IsolatedTextExtractor(
            Microsoft.Extensions.Options.Options.Create(new ExtractorOptions
            {
                Parameters = new ExtractorParameters { HelperPath = "/nonexistent/gert-extract-xyz" },
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IsolatedTextExtractor>.Instance);

        using var content = new MemoryStream([0x25, 0x50, 0x44, 0x46]); // "%PDF"
        var result = await extractor.ExtractAsync(content, "pdf");

        result.Error.Should().NotBeNull();
        result.Error.Should().Contain("not available");
        result.Pages.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_UnsupportedType_Fails()
    {
        var extractor = new IsolatedTextExtractor(
            Microsoft.Extensions.Options.Options.Create(new ExtractorOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IsolatedTextExtractor>.Instance);

        using var content = new MemoryStream([1, 2, 3]);
        var result = await extractor.ExtractAsync(content, "txt");

        result.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadCappedAsync_StreamOverCap_StopsExactlyAtCap()
    {
        // F7 host-side enforcement: a flooding helper stream yields only `cap` bytes.
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(new string('a', 5000)));

        var text = await IsolatedTextExtractor.ReadCappedAsync(stream, maxBytes: 100, CancellationToken.None);

        text.Should().HaveLength(100);
        text.Should().Be(new string('a', 100));
    }

    [Fact]
    public async Task ReadCappedAsync_StreamUnderCap_ReturnsAllBytes()
    {
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("short output"));

        var text = await IsolatedTextExtractor.ReadCappedAsync(stream, maxBytes: 1024, CancellationToken.None);

        text.Should().Be("short output");
    }

    [Fact]
    public async Task ReadCappedAsync_HonoursCapAcrossMultipleChunks()
    {
        // A drip-fed stream forces several ReadAsync round-trips, so the cap must be
        // enforced DURING the loop - not by trimming a single fully-buffered read.
        using var stream = new ChunkedStream(System.Text.Encoding.ASCII.GetBytes(new string('b', 4000)), chunkSize: 7);

        var text = await IsolatedTextExtractor.ReadCappedAsync(stream, maxBytes: 50, CancellationToken.None);

        text.Should().HaveLength(50);
        text.Should().Be(new string('b', 50));
    }

    /// <summary>A stream that returns at most <c>chunkSize</c> bytes per read, forcing the cap loop to iterate.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _pos;

        public ChunkedStream(byte[] data, int chunkSize)
        {
            _data = data;
            _chunkSize = chunkSize;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _data.Length - _pos;
            if (remaining <= 0)
            {
                return 0;
            }

            var n = Math.Min(Math.Min(count, _chunkSize), remaining);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
