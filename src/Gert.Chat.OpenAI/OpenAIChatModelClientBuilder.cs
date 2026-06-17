using Gert.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gert.Chat.OpenAI;

/// <summary>
/// The <c>OpenAI</c> chat-client plugin (<see cref="IChatModelClientBuilder"/>): builds an
/// <see cref="OpenAIChatModelClient"/> for one configured provider slug. Registered keyed by
/// its <see cref="Type"/> in <c>AddGertChatOpenAI</c>; the generic <see cref="ChatClientFactory"/>
/// resolves it for any <c>Gert:Chat:Providers</c> entry whose <c>Type</c> is <c>OpenAI</c>. The
/// plugin owns the OpenAI option shape: each slug's connection + sampling is bound as named
/// <see cref="ChatProviderParameters"/> options (keyed by the slug), and its transport is the
/// per-slug named <c>HttpClient</c> - both wired by the registrar.
/// </summary>
public sealed class OpenAIChatModelClientBuilder : IChatModelClientBuilder
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<ChatProviderParameters> _parameters;
    private readonly ILoggerFactory _loggerFactory;

    public OpenAIChatModelClientBuilder(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<ChatProviderParameters> parameters,
        ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc />
    public string Type => "OpenAI";

    /// <inheritdoc />
    public IChatModelClient Build(string providerId) => new OpenAIChatModelClient(
        _httpFactory.CreateClient(OpenAIChatModelClient.HttpClientNameFor(providerId)),
        _parameters.Get(providerId),
        _loggerFactory.CreateLogger<OpenAIChatModelClient>());
}
