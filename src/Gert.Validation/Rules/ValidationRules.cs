using System.Globalization;
using FluentValidation;
using Gert.Model.Chat;

namespace Gert.Validation.Rules;

/// <summary>
/// Shared, DRY building blocks for the per-DTO validators (testing.md section 5
/// threat table). Centralizing the control-character / bidi-override and length
/// rules here means every text field is held to the <b>same</b> security bar, and
/// a tightened rule lands everywhere at once. Pure predicate helpers plus
/// <see cref="IRuleBuilder{T, TProperty}"/> extensions - no I/O, no state.
/// </summary>
public static class ValidationRules
{
    /// <summary>Default cap for short single-line text (titles, names, languages).</summary>
    public const int ShortTextMax = 200;

    /// <summary>Default cap for medium free text (instructions, descriptions).</summary>
    public const int MediumTextMax = 10_000;

    /// <summary>Default cap for long free text - a chat message / artifact body (DoS brake).</summary>
    public const int LongTextMax = 100_000;

    /// <summary>Hard upper bound on an identifier-shaped string (model id, language tag).</summary>
    public const int IdentifierMax = 128;

    /// <summary>Max inline attachments (images + text files) on one message.</summary>
    public const int AttachmentMaxCount = 6;

    /// <summary>Max filename length on a text-file attachment (metadata/DoS brake; mirrors the upload cap).</summary>
    public const int AttachmentNameMaxChars = 255;

    /// <summary>
    /// Max base64 length of one attachment (~6 MB decoded - far above what the
    /// composer's client-side downscale produces; the DoS brake, not a target).
    /// </summary>
    public const int AttachmentDataMaxChars = 8_000_000;

    /// <summary>
    /// True if <paramref name="value"/> is an allowed attachment image MIME type. Delegates to the
    /// single source - <see cref="AttachmentKinds.IsAllowedImageMime"/> - so the validator and the
    /// prompt path can never disagree on which images ride to a vision model.
    /// </summary>
    public static bool IsAllowedImageMime(string? value) => AttachmentKinds.IsAllowedImageMime(value);

    /// <summary>
    /// True if <paramref name="value"/> is non-empty, well-formed base64 - checked
    /// without decoding (no large transient buffers on the validation hot path).
    /// </summary>
    public static bool IsWellFormedBase64(string? value) =>
        !string.IsNullOrEmpty(value) && System.Buffers.Text.Base64.IsValid(value.AsSpan());

    /// <summary>
    /// True if <paramref name="value"/> contains a control character that is never
    /// legitimate in user text. Tab/newline/carriage-return are allowed (multi-line
    /// content); everything else in the C0/C1 ranges (NUL, BEL, ESC, form-feed,
    /// vertical-tab, DEL) is refused (log-injection, parser confusion, truncation).
    /// </summary>
    public static bool ContainsForbiddenControlChar(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is '\t' or '\n' or '\r')
            {
                continue;
            }

            // char.IsControl covers C0 (U+0000-U+001F), DEL (U+007F) and C1 (U+0080-U+009F).
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if <paramref name="value"/> carries a Unicode bidi-override / directional
    /// formatting character usable for display spoofing (an RTL-override can render a
    /// name's extension backwards). Covers the LRM/RLM/ALM marks, the LRE/RLE/PDF/
    /// LRO/RLO embeddings (U+202A..U+202E), and the isolate set (U+2066..U+2069).
    /// </summary>
    public static bool ContainsBidiOverride(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            int cp = c;

            // U+200E LRM, U+200F RLM, U+061C ALM.
            if (cp == 0x200E || cp == 0x200F || cp == 0x061C)
            {
                return true;
            }

            // U+202A..U+202E: LRE, RLE, PDF, LRO, RLO.
            if (cp >= 0x202A && cp <= 0x202E)
            {
                return true;
            }

            // U+2066..U+2069: LRI, RLI, FSI, PDI.
            if (cp >= 0x2066 && cp <= 0x2069)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Safe-text rule: not null, not whitespace-only, within <paramref name="maxLength"/>,
    /// and free of forbidden control + bidi-override characters. The single rule every
    /// human-authored text field (message, title, name, instructions) is held to.
    /// </summary>
    public static IRuleBuilderOptions<T, string> SafeText<T>(
        this IRuleBuilder<T, string> rule,
        int maxLength)
    {
        return rule
            .Must(v => !string.IsNullOrWhiteSpace(v))
                .WithMessage("Must not be empty or whitespace-only.")
                .WithErrorCode("text.empty")
            .Must(v => v is null || v.Length <= maxLength)
                .WithMessage($"Must be at most {maxLength} characters.")
                .WithErrorCode("text.too_long")
            .Must(v => !ContainsForbiddenControlChar(v))
                .WithMessage("Must not contain control characters.")
                .WithErrorCode("text.control_char")
            .Must(v => !ContainsBidiOverride(v))
                .WithMessage("Must not contain bidirectional-override characters.")
                .WithErrorCode("text.bidi_override");
    }

    /// <summary>
    /// Optional safe-text rule for a nullable field that may legitimately be unset
    /// (PATCH semantics): <c>null</c> passes; a supplied value must clear
    /// <see cref="SafeText{T}"/>. An explicitly empty/whitespace value is still rejected.
    /// </summary>
    public static IRuleBuilderOptions<T, string?> OptionalSafeText<T>(
        this IRuleBuilder<T, string?> rule,
        int maxLength)
    {
        return rule
            .Must(v => v is null || !string.IsNullOrWhiteSpace(v))
                .WithMessage("Must not be empty or whitespace-only.")
                .WithErrorCode("text.empty")
            .Must(v => v is null || v.Length <= maxLength)
                .WithMessage($"Must be at most {maxLength} characters.")
                .WithErrorCode("text.too_long")
            .Must(v => v is null || !ContainsForbiddenControlChar(v))
                .WithMessage("Must not contain control characters.")
                .WithErrorCode("text.control_char")
            .Must(v => v is null || !ContainsBidiOverride(v))
                .WithMessage("Must not contain bidirectional-override characters.")
                .WithErrorCode("text.bidi_override");
    }

    /// <summary>
    /// True if <paramref name="value"/> is a well-formed conversation/document
    /// identifier: a GUID in the canonical "D" format (8-4-4-4-12, the shape
    /// <c>Guid.NewGuid().ToString("D")</c> produces and every server-generated id
    /// uses). Pinned to "D" - not <see cref="Guid.TryParse(string?, out Guid)"/> -
    /// because the storage guards (<c>StorageKeys.ValidatePid</c>,
    /// <c>SqliteDatabasePaths.ValidatePid</c>) require exactly that shape, so an
    /// "N"/"B"/"P"-format GUID must 400 at this boundary instead of 500 at storage.
    /// IDOR is structural (key from token), so this is defence-in-depth before an id
    /// reaches a repo (testing.md section 5).
    /// </summary>
    public static bool IsWellFormedId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Guid.TryParseExact(value, "D", out _);

    /// <summary>
    /// True if <paramref name="value"/> is a valid project id: a GUID or the literal
    /// <c>default</c> (configuration.md section 2.5; security F6's <c>pid</c> analog).
    /// </summary>
    public static bool IsWellFormedProjectId(string? value) =>
        string.Equals(value, "default", StringComparison.Ordinal) || IsWellFormedId(value);

    /// <summary>
    /// True if <paramref name="value"/> matches the admin folder-key shape
    /// <c>^[0-9a-f]{64}$</c> (a lowercase sha256 hex) - the F6 gate that must pass
    /// <b>before</b> <c>{key}</c> is path-joined and used to delete a user's data.
    /// </summary>
    public static bool IsWellFormedAdminKey(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            var isHex = c is >= '0' and <= '9' || c is >= 'a' and <= 'f';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True if <paramref name="value"/> is a safe model-id / language-tag token:
    /// non-empty, bounded, and drawn from a conservative ASCII charset
    /// (letters, digits, and . _ - : / +) - no whitespace, control, or homoglyph
    /// code points that could steer to an unintended model.
    /// </summary>
    public static bool IsSafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > IdentifierMax)
        {
            return false;
        }

        foreach (var c in value)
        {
            var ok = c is >= 'a' and <= 'z'
                     || c is >= 'A' and <= 'Z'
                     || c is >= '0' and <= '9'
                     || c is '.' or '_' or '-' or ':' or '/' or '+';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The model-id rule shared by every DTO that carries one (send/create/update conversation,
    /// project defaults, settings): the safe-token shape <see cref="IsSafeIdentifier"/>, then an
    /// allow-list against the configured providers (<paramref name="catalog"/>) so an unknown slug
    /// 400s at the boundary instead of silently falling back to the default at resolve time. The
    /// caller still gates with <c>.When(value is not null)</c> - a null model_id means "use the
    /// default" and is always allowed. Permissive when no catalog is wired (zero-config dev).
    /// </summary>
    public static IRuleBuilderOptions<T, string> ModelId<T>(
        this IRuleBuilder<T, string> rule, IModelIdCatalog? catalog)
    {
        return rule
            .Must(IsSafeIdentifier)
                .WithMessage("Model id must be a safe identifier token.")
                .WithErrorCode("model_id.invalid")
            .Must(id => IsKnownModelId(catalog, id))
                .WithMessage("Model id is not a configured provider.")
                .WithErrorCode("model_id.unknown");
    }

    /// <summary>
    /// True if <paramref name="id"/> is an acceptable model_id: the <c>default</c> sentinel, or a
    /// configured provider slug. Permissive (true) when <paramref name="catalog"/> is null or empty
    /// - an unwired/zero-config catalog has nothing to allow-list against.
    /// </summary>
    public static bool IsKnownModelId(IModelIdCatalog? catalog, string id) =>
        catalog is null
        || catalog.ModelIds.Count == 0
        || id == ChatProviderInfo.DefaultId
        || catalog.ModelIds.Contains(id);

    /// <summary>
    /// The lowercase (invariant) extension of <paramref name="filename"/> without the
    /// dot, or the empty string if there is none.
    /// </summary>
    public static string ExtensionOf(string filename)
    {
        var ext = Path.GetExtension(filename ?? string.Empty);
        return ext.Length == 0
            ? string.Empty
            : ext[1..].ToLower(CultureInfo.InvariantCulture);
    }
}
