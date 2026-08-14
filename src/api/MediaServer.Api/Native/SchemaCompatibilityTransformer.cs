using System.Text.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace MediaServer.Api.Native;

/// <summary>
/// Rewrites the two shapes ASP.NET emits that a code generator cannot read.
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
///
/// The second shape is an enum with no type. ASP.NET writes <c>{ "enum": ["DirectPlay", "Remux"] }</c>
/// and nothing else, and a generator that cannot tell what the values *are* produces an untyped value
/// container rather than a Swift enum — so <c>decision</c> and <c>transport</c>, the two fields that
/// decide how a title is delivered, arrived with no type at all. A nullable enum is worse again: the
/// <c>null</c> joins the value list, so the type has a member that is not a value.
///
/// Both are fixed by saying what was already true: the values are strings, and a <c>null</c> among them
/// means the property may be absent rather than that "null" is a case.
/// </summary>
internal sealed class SchemaCompatibilityTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        foreach (var schema in document.Components?.Schemas?.Values ?? [])
        {
            Rewrite(schema);
            Retype(schema);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gives a bare enum the type its values already have, and drops a <c>null</c> member — nullability
    /// belongs to the property that refers to the enum, which the rewrite above has already made
    /// optional.
    /// </summary>
    private static void Retype(IOpenApiSchema schema)
    {
        if (schema is not OpenApiSchema concrete || concrete.Enum is not { Count: > 0 } values)
        {
            return;
        }

        var nulls = values.Where(value => value is null || value.GetValueKind() == JsonValueKind.Null).ToList();
        foreach (var empty in nulls)
        {
            values.Remove(empty);
        }

        if (concrete.Type is null && values.All(value => value.GetValueKind() == JsonValueKind.String))
        {
            concrete.Type = JsonSchemaType.String;
        }
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
