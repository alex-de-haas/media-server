using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace MediaServer.Api.Native;

/// <summary>
/// Rewrites a nullable reference so that a generator can see it.
///
/// ASP.NET describes <c>UserItemDataDto?</c> as <c>oneOf: [{ "type": "null" }, { "$ref": … }]</c>, which
/// is legal OpenAPI 3.1 and which <c>swift-openapi-generator</c> declines to read: a bare <c>null</c>
/// schema is not a type it supports, so it **skips the whole property** with a warning nobody sees.
///
/// That is worse than either failure it might have had. The point of generating a client is that a
/// change to this surface becomes a compile error rather than a decoding failure on a television — and a
/// property that silently vanishes is neither. Eight of them had vanished when this was written,
/// including <c>LibraryItemDto.userData</c>, which carries resume position and watched state, and
/// <c>NativePlaybackResolution.transport</c>, which decides how playback is delivered.
///
/// The rewrite is to the form every generator does read: the reference on its own, with the property no
/// longer required.
///
/// It is a trade rather than a strict equivalence, and worth being exact about. Nothing this server
/// sends changes. But the new shape describes an <em>absent</em> field where the old one also described
/// an explicit <c>null</c>, so a client generated from it may reject a literal null it would previously
/// have accepted. That is the right way round: absence is what a reader has to handle anyway, and a
/// description slightly narrower than the wire costs far less than eight properties that are not
/// described at all.
/// </summary>
internal sealed class NullableRefSchemaTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        foreach (var schema in document.Components?.Schemas?.Values ?? [])
        {
            Rewrite(schema);
        }

        return Task.CompletedTask;
    }

    private static void Rewrite(IOpenApiSchema schema)
    {
        if (schema is not OpenApiSchema concrete || concrete.Properties is null)
        {
            return;
        }

        foreach (var name in concrete.Properties.Keys.ToList())
        {
            if (Unwrap(concrete.Properties[name]) is not { } reference)
            {
                continue;
            }

            concrete.Properties[name] = reference;

            // A nullable value is exactly an optional one as far as a reader is concerned, and "may be
            // absent" is the half of it every generator understands.
            concrete.Required?.Remove(name);
        }
    }

    /// <summary>The referenced half of a two-branch union whose other half is <c>null</c>, if that is what
    /// this is.</summary>
    private static IOpenApiSchema? Unwrap(IOpenApiSchema property)
    {
        if (property.OneOf is not { Count: 2 } branches)
        {
            return null;
        }

        var nulls = branches.Where(branch => branch.Type == JsonSchemaType.Null).ToList();
        var referenced = branches.OfType<OpenApiSchemaReference>().ToList();

        return nulls.Count == 1 && referenced.Count == 1 ? referenced[0] : null;
    }
}
