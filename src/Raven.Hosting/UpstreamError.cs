using System.Net;

namespace Raven.Hosting;

/// <summary>A failed call to another service, as it is logged: the status and the start of what it answered</summary>
public static class UpstreamError
{
    public static string Describe(HttpStatusCode code, string body)
    {
        var status = $"{(int)code} {code}";
        return string.IsNullOrWhiteSpace(body) ? status : $"{status}: {(body.Length > 200 ? body[..200] : body)}";
    }
}
