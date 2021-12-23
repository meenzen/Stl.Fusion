using System.Net.Http.Headers;
using RestEase;

namespace Stl.Fusion.Client.RestEase.Internal;

public class FusionRequestBodySerializer : RequestBodySerializer
{
    public ITextWriter Writer { get; init; } = SystemJsonSerializer.Default.Writer;
    public string ContentType { get; init; } = "application/json";

    public override HttpContent? SerializeBody<T>(T body, RequestBodySerializerInfo info)
    {
        if (body == null)
            return null;

        var content = new StringContent(Writer.Write<T>(body));
        if (content.Headers.ContentType == null)
            content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
        else
            content.Headers.ContentType.MediaType = ContentType;
        return content;
    }
}
