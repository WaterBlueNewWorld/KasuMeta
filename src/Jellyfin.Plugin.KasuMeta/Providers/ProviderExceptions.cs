namespace Jellyfin.Plugin.KasuMeta.Providers;

/// <summary>The provider has no API key configured; nothing was sent.</summary>
public sealed class ProviderNotConfiguredException : Exception
{
    public ProviderNotConfiguredException()
    {
    }

    public ProviderNotConfiguredException(string message)
        : base(message)
    {
    }

    public ProviderNotConfiguredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The provider rejected the API key (HTTP 401). Retrying won't help.</summary>
public sealed class ProviderAuthenticationException : Exception
{
    public ProviderAuthenticationException()
    {
    }

    public ProviderAuthenticationException(string message)
        : base(message)
    {
    }

    public ProviderAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
